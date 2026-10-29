-- SSBU Arena replays (applied to the Supabase project ssbu-arena as migration "replays", 2026-10-24). Recorded by the VPS lobby
-- (tools/net/rb_replay_rec.c), uploaded by tools/net/replay_uploader.py through the edge function replay-ingest (shared ingest
-- secret: only its SHA-256 is stored here, the secret itself is a root-only file on the VPS), read by the launcher (signed-in).
create table public.replays (
  id text primary key,                 -- <utc yyyymmdd-hhmmss>-<match id hex>
  match_id bigint not null,
  played_at timestamptz not null,
  uploaded_at timestamptz not null default now(),
  p1_name text not null, p2_name text not null,
  p1_uid uuid, p2_uid uuid,
  p1_chara text not null, p2_chara text not null,   -- ui_chara ids, 16 hex digits
  p1_colour smallint not null, p2_colour smallint not null,
  stage text not null, stage_form smallint not null default 0,
  rule_kind smallint not null, stocks smallint not null,
  frames integer not null, duration_ms integer not null,
  build text not null, game_build smallint not null, proto smallint not null,
  flags integer not null, desync_frame integer not null default 0,
  winner smallint,                     -- 0 / 1, null = not known
  size integer not null,
  path text not null                   -- object in the storage bucket "replays"
);
create index replays_played_at on public.replays (played_at desc);
create index replays_p1_uid on public.replays (p1_uid);
create index replays_p2_uid on public.replays (p2_uid);
alter table public.replays enable row level security;
create policy "replays are readable by signed-in players" on public.replays for select to authenticated using (true);
revoke all on public.replays from anon;
revoke insert, update, delete, truncate, references, trigger on public.replays from authenticated;
grant select on public.replays to authenticated;

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('replays', 'replays', false, 4194304, array['application/octet-stream']);
create policy "replay files are readable by signed-in players" on storage.objects for select to authenticated using (bucket_id = 'replays');

create table private.replay_ingest (secret_sha256 text not null);
alter table private.replay_ingest enable row level security;
revoke all on private.replay_ingest from public, anon, authenticated;
-- insert into private.replay_ingest values ('<sha256 hex of the VPS file /etc/ssbu-rblobby/replay_ingest_secret, without its newline>');

create function public.replay_ingest_ok(secret text) returns boolean
language sql stable security definer set search_path = '' as $$
  select exists (select 1 from private.replay_ingest r where r.secret_sha256 = encode(extensions.digest(secret, 'sha256'), 'hex'));
$$;
revoke execute on function public.replay_ingest_ok(text) from public, anon, authenticated;
grant execute on function public.replay_ingest_ok(text) to service_role;
