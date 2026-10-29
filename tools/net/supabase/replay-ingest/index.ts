// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena replays: the Supabase Edge Function "replay-ingest" (deployed with verify_jwt off: it checks its own secret).
// Called only by the VPS (tools/net/replay_uploader.py) with the shared ingest secret (header x-replay-secret; the database
// keeps only its SHA-256, private.replay_ingest, checked by rpc replay_ingest_ok). The function holds the project's service
// role (its own environment), so the VPS never needs the service key.
//   POST ?id=<yyyymmdd-hhmmss-matchid>   body = the .ssbr file with a deflated body (codec 1): stored in the bucket "replays"
//                                        and one row in public.replays (the metadata is read from the file's header here)
//   POST ?action=prune&days=30&keep=5000 replays older than <days> (and beyond the newest <keep>) deleted, files and rows
//   POST ?action=delete&id=<id>          one replay deleted (its files and its row)
// (2026-10-29, replay viewer) a row can also hold a player's game recording (.ssbv, column ssbv_path, uploaded by the
// launcher) and can exist before the lobby's file (path null): both files are deleted with the row. A new lobby file of a
// match that already has a row is merged into that row by the database itself (trigger replays_merge, migration
// replay_state_and_visibility): the ingest below is unchanged.
// The .ssbr layout is documented in tools/net/rb_replay_rec.c.
const URL_ = Deno.env.get("SUPABASE_URL")!;
const KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const H = { apikey: KEY, Authorization: `Bearer ${KEY}` };
const HDR = 0x1000;
const MAX = 4 * 1024 * 1024;

function json(status: number, o: unknown) {
  return new Response(JSON.stringify(o), { status, headers: { "Content-Type": "application/json" } });
}
function cstr(b: Uint8Array, off: number, n: number): string {
  let e = off;
  while (e < off + n && b[e]) e++;
  return new TextDecoder().decode(b.subarray(off, e));
}
const hex16 = (v: bigint) => v.toString(16).padStart(16, "0");
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

async function secretOk(secret: string | null): Promise<boolean> {
  if (!secret || secret.length > 200) return false;
  const r = await fetch(`${URL_}/rest/v1/rpc/replay_ingest_ok`, {
    method: "POST", headers: { ...H, "Content-Type": "application/json" }, body: JSON.stringify({ secret }),
  });
  return r.ok && (await r.json()) === true;
}

function parse(b: Uint8Array) {
  if (b.length < HDR || cstr(b, 0, 4) !== "SSBR") throw new Error("not an SSBR replay");
  const v = new DataView(b.buffer, b.byteOffset, b.byteLength);
  const version = v.getUint16(4, true), hsize = v.getUint16(6, true), codec = b[0xf];
  const bodySize = v.getUint32(0x24, true);
  if (version !== 1 || hsize !== HDR || codec !== 1) throw new Error(`version ${version}, header ${hsize}, codec ${codec}: not accepted`);
  if (HDR + bodySize !== b.length) throw new Error("size does not match the header");
  const m = 0x100;
  const uid = (o: number) => { const s = cstr(b, o, 40); return UUID.test(s) ? s : null; };
  return {
    match_id: v.getUint32(0x10, true),
    frames: v.getUint32(0x14, true),
    played_at: new Date(Number(v.getBigInt64(0x18, true)) * 1000).toISOString(),
    duration_ms: v.getUint32(0x20, true),
    desync_frame: v.getUint32(0x2c, true),
    winner: b[0x30] === 0xff ? null : b[0x30],
    flags: v.getUint32(8, true),
    game_build: v.getInt16(0xc, true),
    proto: b[0xe],
    build: cstr(b, 0x38, 40),
    p1_name: cstr(b, 0x60, 32), p2_name: cstr(b, 0x80, 32),
    p1_uid: uid(0xa0), p2_uid: uid(0xc8),
    p1_chara: hex16(v.getBigUint64(m, true)), p2_chara: hex16(v.getBigUint64(m + 8, true)),
    p1_colour: b[m + 16], p2_colour: b[m + 17],
    stage: hex16(v.getBigUint64(m + 0x40, true)), stage_form: v.getUint32(m + 0x48, true) & 0x7fff,
    rule_kind: b[m + 0x61], stocks: b[m + 0x62],
  };
}

async function ingest(id: string, body: Uint8Array) {
  if (!/^\d{8}-\d{6}-[0-9a-f]{8}$/.test(id)) return json(400, { error: "bad id" });
  if (body.length > MAX) return json(413, { error: "too large" });
  let meta;
  try { meta = parse(body); } catch (e) { return json(400, { error: String(e) }); }
  const path = `${id.slice(0, 6)}/${id}.ssbr`;
  const up = await fetch(`${URL_}/storage/v1/object/replays/${path}`, {
    method: "POST", headers: { ...H, "Content-Type": "application/octet-stream", "x-upsert": "true" }, body,
  });
  if (!up.ok) return json(502, { error: "storage", status: up.status, detail: await up.text() });
  const row = { id, ...meta, size: body.length, path };
  const ins = await fetch(`${URL_}/rest/v1/replays`, {
    method: "POST", headers: { ...H, "Content-Type": "application/json", Prefer: "resolution=merge-duplicates,return=minimal" },
    body: JSON.stringify(row),
  });
  if (!ins.ok) return json(502, { error: "table", status: ins.status, detail: await ins.text() });
  return json(200, { ok: true, id, path, size: body.length });
}

async function prune(days: number, keep: number) {
  const cutoff = new Date(Date.now() - days * 86400e3).toISOString();
  const old = await (await fetch(`${URL_}/rest/v1/replays?select=id,path,ssbv_path&played_at=lt.${cutoff}&limit=1000`, { headers: H })).json();
  const extra = await (await fetch(`${URL_}/rest/v1/replays?select=id,path,ssbv_path&order=played_at.desc&offset=${keep}&limit=1000`, { headers: H })).json();
  const rows = new Map<string, string[]>();
  for (const r of [...(old ?? []), ...(extra ?? [])]) rows.set(r.id, [r.path, r.ssbv_path].filter((x) => !!x));
  if (!rows.size) return json(200, { ok: true, deleted: 0 });
  const files = [...rows.values()].flat();
  if (files.length) {
    const del = await fetch(`${URL_}/storage/v1/object/replays`, {
      method: "DELETE", headers: { ...H, "Content-Type": "application/json" }, body: JSON.stringify({ prefixes: files }),
    });
    if (!del.ok) return json(502, { error: "storage delete", status: del.status });
  }
  const ids = [...rows.keys()].map((s) => `"${s}"`).join(",");
  const d2 = await fetch(`${URL_}/rest/v1/replays?id=in.(${ids})`, { method: "DELETE", headers: H });
  return json(d2.ok ? 200 : 502, { ok: d2.ok, deleted: rows.size });
}

Deno.serve(async (req: Request) => {
  if (req.method !== "POST") return json(405, { error: "POST only" });
  if (!(await secretOk(req.headers.get("x-replay-secret")))) return json(401, { error: "not allowed" });
  const u = new URL(req.url);
  if (u.searchParams.get("action") === "delete") {        // one replay (moderation, tests): its file and its row
    const id = u.searchParams.get("id") ?? "";
    if (!/^\d{8}-\d{6}-[0-9a-f]{8}$/.test(id)) return json(400, { error: "bad id" });
    const path = `${id.slice(0, 6)}/${id}.ssbr`;
    const cur = await (await fetch(`${URL_}/rest/v1/replays?select=path,ssbv_path&id=eq.${id}`, { headers: H })).json();
    const prefixes = [path, ...((cur ?? []).flatMap((r: { path: string | null; ssbv_path: string | null }) => [r.path, r.ssbv_path]))].filter((x) => !!x);
    await fetch(`${URL_}/storage/v1/object/replays`, { method: "DELETE", headers: { ...H, "Content-Type": "application/json" }, body: JSON.stringify({ prefixes: [...new Set(prefixes)] }) });
    const d = await fetch(`${URL_}/rest/v1/replays?id=eq.${id}`, { method: "DELETE", headers: H });
    return json(d.ok ? 200 : 502, { ok: d.ok, id });
  }
  if (u.searchParams.get("action") === "prune") {
    const days = Math.max(1, Number(u.searchParams.get("days") ?? 30));
    const keep = Math.max(10, Number(u.searchParams.get("keep") ?? 5000));
    return await prune(days, keep);
  }
  return await ingest(u.searchParams.get("id") ?? "", new Uint8Array(await req.arrayBuffer()));
});
