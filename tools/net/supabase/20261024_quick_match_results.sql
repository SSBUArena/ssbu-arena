-- SSBU Arena: quick-match results (the lobby is the referee: tools/net/rb_ranked.c; the VPS writer: tools/net/arena_results.py).
-- Applied to project ssbu-arena as migration "quick_match_results". Kept here as the reference copy.
--
-- public.matches: one row per decided quick match that started. Written ONLY server side: either the service role (bypasses
-- RLS) or public.record_match(payload, sig), whose HMAC-SHA256 signature needs private.results_secret (a copy lives only on the
-- VPS, root-only file). Clients (anon / authenticated) have no INSERT / UPDATE / DELETE on it; signed-in players can read it.
-- No internet address is ever stored.
-- Also: the default table grants that let anon / authenticated UPDATE / INSERT / DELETE public.profiles (incl. elo) are revoked
-- (there was no policy using them; the elo column must never be client-writable).

create table if not exists public.matches (
    id          bigint primary key check (id > 0),       -- the lobby's game id
    played_at   timestamptz not null,
    decided_at  timestamptz,
    p1          uuid references public.profiles (id) on delete set null,
    p2          uuid references public.profiles (id) on delete set null,
    p1_name     text not null,
    p2_name     text not null,
    p1_fighter  text,
    p2_fighter  text,
    stage       text,
    p1_stocks   smallint,
    p2_stocks   smallint,
    winner      uuid references public.profiles (id) on delete set null,
    result      text not null check (result in ('recorded', 'forfeit', 'no_contest', 'void')),
    reason      text,
    frames      integer,
    build       text,
    region      text not null default 'eu',
    created_at  timestamptz not null default now()
);
create index if not exists matches_p1_played on public.matches (p1, played_at desc);
create index if not exists matches_p2_played on public.matches (p2, played_at desc);
create index if not exists matches_winner on public.matches (winner);

alter table public.matches enable row level security;
revoke all on table public.matches from anon, authenticated;
grant select on table public.matches to authenticated;
drop policy if exists "matches are readable by signed-in players" on public.matches;
create policy "matches are readable by signed-in players" on public.matches for select to authenticated using (true);

create table if not exists private.results_secret (secret text not null);
revoke all on table private.results_secret from public, anon, authenticated;

create or replace function public.record_match(payload text, sig text)
returns text
language plpgsql
security definer
set search_path = ''
as $$
declare
    k text;
    j jsonb;
    u1 uuid; u2 uuid; uw uuid;
begin
    select s.secret into k from private.results_secret s limit 1;
    if k is null then raise exception 'results writer not configured' using errcode = '42501'; end if;
    if sig is null or length(payload) > 8000 or encode(extensions.hmac(payload, k, 'sha256'), 'hex') <> lower(sig) then
        raise exception 'bad signature' using errcode = '42501';
    end if;
    j := payload::jsonb;
    select p.id into u1 from public.profiles p where p.id = (j->>'p1')::uuid;
    select p.id into u2 from public.profiles p where p.id = (j->>'p2')::uuid;
    if j->>'winner' is not null then select p.id into uw from public.profiles p where p.id = (j->>'winner')::uuid; end if;
    insert into public.matches (id, played_at, decided_at, p1, p2, p1_name, p2_name, p1_fighter, p2_fighter, stage, p1_stocks, p2_stocks,
                                winner, result, reason, frames, build, region)
    values ((j->>'id')::bigint, (j->>'played_at')::timestamptz, (j->>'decided_at')::timestamptz, u1, u2,
            left(j->>'p1_name', 40), left(j->>'p2_name', 40), left(j->>'p1_fighter', 40), left(j->>'p2_fighter', 40), left(j->>'stage', 40),
            (j->>'p1_stocks')::smallint, (j->>'p2_stocks')::smallint, uw, j->>'result', left(j->>'reason', 120),
            (j->>'frames')::integer, left(j->>'build', 80), coalesce(left(j->>'region', 16), 'eu'))
    on conflict (id) do nothing;
    return 'ok';
end;
$$;
revoke all on function public.record_match(text, text) from public;
grant execute on function public.record_match(text, text) to anon, authenticated, service_role;

-- profiles: no client writes (the trigger handle_new_user is SECURITY DEFINER and keeps working)
revoke insert, update, delete, truncate on table public.profiles from anon, authenticated;
revoke update (elo) on table public.profiles from anon, authenticated;      -- (column level too: explicit)

-- Migration "record_match_anon_only": only anon (the VPS writer's publishable key) may call record_match.
-- Migration "record_match_tolerant_ids": account ids that are not UUIDs of a profile (test tickets, deleted accounts) are stored
-- as null (the names stay) instead of refusing the row:
--     if coalesce(j->>'p1', '') ~ '^[0-9a-fA-F]{8}-...-[0-9a-fA-F]{12}$' then select p.id into u1 from public.profiles p where p.id = (j->>'p1')::uuid; end if;
--     (same for p2 and winner); then the same insert ... on conflict (id) do nothing.
revoke all on function public.record_match(text, text) from public, authenticated;
grant execute on function public.record_match(text, text) to anon, service_role;
