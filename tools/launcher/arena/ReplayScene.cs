// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena launcher: the game's own fighters and stage under the replay's hit boxes (ReplayView draws those over this). The files
// come from the player's data.arc (tools/replay/replay_assets.py, build\replay_assets; layouts there), made when a replay is opened.
// Each frame the fighter's bones are posed from the motion and frame the game recorded, its visible meshes skinned here and
// placed at the recorded position, seen from the side with the same scale as the hit boxes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Arena
{
    sealed class RvBone { public int Parent; public float[] Local, Inv; }
    sealed class RvMesh { public int Tex, Vis; public bool Default; public float[] Pos, Nor; public byte[] SkinB; public float[] SkinW; public Int32Collection Idx; public PointCollection Uv; public GeometryModel3D Model; public MeshGeometry3D Geo; }
    sealed class RvModel
    {
        public RvBone[] Bones; public string[] Vis; public List<RvMesh> Meshes = new List<RvMesh>(); public List<string> Tex = new List<string>();

        public static RvModel Read(string dir)
        {
            using (var b = new BinaryReader(new MemoryStream(File.ReadAllBytes(Path.Combine(dir, "model.rvm")))))
            {
                var m = new RvModel();
                if (new string(b.ReadChars(4)) != "RVM2") throw new InvalidDataException("model file");
                m.Bones = new RvBone[b.ReadInt32()];
                for (int i = 0; i < m.Bones.Length; i++) m.Bones[i] = new RvBone { Parent = b.ReadInt32(), Local = Floats(b, 16), Inv = Floats(b, 16) };
                int nt = b.ReadInt32();
                for (int i = 0; i < nt; i++) m.Tex.Add(Encoding.UTF8.GetString(b.ReadBytes(b.ReadUInt16())));
                m.Vis = new string[b.ReadInt32()];
                for (int i = 0; i < m.Vis.Length; i++) m.Vis[i] = Encoding.UTF8.GetString(b.ReadBytes(b.ReadUInt16()));
                int nm = b.ReadInt32();
                for (int i = 0; i < nm; i++)
                {
                    int tex = b.ReadInt32(), vis = b.ReadInt32(), def = b.ReadInt32(), nv = b.ReadInt32(), ni = b.ReadInt32();
                    var g = new RvMesh { Tex = tex, Vis = vis, Default = def != 0, Pos = Floats(b, nv * 3), Nor = Floats(b, nv * 3) };
                    var uv = Floats(b, nv * 2);
                    g.SkinB = b.ReadBytes(nv * 4); g.SkinW = Floats(b, nv * 4);
                    var idx = new Int32Collection(ni);
                    for (int k = 0; k < ni; k++) idx.Add(b.ReadInt32());
                    idx.Freeze(); g.Idx = idx;
                    var pc = new PointCollection(nv);
                    for (int k = 0; k < nv; k++) pc.Add(new Point(uv[k * 2], uv[k * 2 + 1]));
                    pc.Freeze(); g.Uv = pc;
                    m.Meshes.Add(g);
                }
                return m;
            }
        }
        public static float[] Floats(BinaryReader b, int n) { var a = new float[n]; Buffer.BlockCopy(b.ReadBytes(n * 4), 0, a, 0, n * 4); return a; }
    }

    sealed class RvMotion
    {
        public Dictionary<int, float[]> Tracks = new Dictionary<int, float[]>();      // bone -> frames x 10 (scale, quaternion, translation)
        public Dictionary<string, byte[]> Vis = new Dictionary<string, byte[]>();

        public static RvMotion Read(string file)
        {
            var d = File.ReadAllBytes(file);
            if (d.Length < 12) return null;
            using (var b = new BinaryReader(new MemoryStream(d)))
            {
                b.ReadInt32(); b.ReadSingle();
                var m = new RvMotion();
                int nt = b.ReadInt32();
                for (int i = 0; i < nt; i++) { int bone = b.ReadUInt16(), fr = b.ReadUInt16(); m.Tracks[bone] = RvModel.Floats(b, fr * 10); }
                int nv = b.ReadInt32();
                for (int i = 0; i < nv; i++) { string name = Encoding.UTF8.GetString(b.ReadBytes(b.ReadUInt16())); m.Vis[name] = b.ReadBytes(b.ReadUInt16()); }
                return m;
            }
        }
    }

    public sealed class ReplayScene
    {
        sealed class Fighter { public RvModel Model; public double Scale = 1; public string Folder; public Dictionary<ulong, RvMotion> Motions = new Dictionary<ulong, RvMotion>(); public Model3DGroup Group; public int Seen = -1; public double SeenFrame = -1; public ulong SeenMotion; public float[] World, Skin; }

        readonly Viewport3D view;
        readonly OrthographicCamera cam = new OrthographicCamera { LookDirection = new Vector3D(0, 0, -1), UpDirection = new Vector3D(0, 1, 0), NearPlaneDistance = 1, FarPlaneDistance = 6000 };
        readonly Model3DGroup root = new Model3DGroup();
        Fighter[] fighters = new Fighter[0];
        bool stageOn, stageLines;
        int moveFirst = -1, moveCount; double moveX, moveY; TranslateTransform3D moveBy; Model3DGroup moveGroup;
        int loadId;

        public ReplayScene(Viewport3D v)
        {
            view = v; view.Camera = cam;
            view.Children.Add(new ModelVisual3D { Content = root });
        }

        public bool On = true;
        public bool StageShown => On && stageOn;
        public bool MovingAsLines => stageLines;      // this stage's moving platforms are drawn by ReplayView
        public bool FighterShown(int k) => On && k < fighters.Length && fighters[k] != null;

        public static string AssetsDir => Path.Combine(Core.Build, "replay_assets");

        public void Clear()
        {
            loadId++;
            fighters = new Fighter[0]; stageOn = stageLines = false; moveFirst = -1; moveGroup = null; root.Children.Clear();
        }

        /// <summary>the files of this replay (made if missing), then the scene. status: what the page says meanwhile; "" when done.</summary>
        public void Load(ReplayState r, Action<string> status)
        {
            Clear();
            int id = loadId;
            string path = r.Path;
            status("Preparing the game's models from your game files ...");
            Task.Run(() => Core.MakeReplayAssets(path)).ContinueWith(t =>
            {
                if (id != loadId) return;
                if (t.IsFaulted || t.Result == null) { status("The game's models could not be prepared: the replay is shown as a diagram."); return; }
                Task.Run(() => Read(t.Result)).ContinueWith(u =>
                {
                    if (id != loadId) return;
                    if (u.IsFaulted) { Updater.Log("replay models: " + u.Exception.GetBaseException().Message); status("The game's models could not be read: the replay is shown as a diagram."); return; }
                    try { Build(u.Result); status(""); }
                    catch (Exception e) { Updater.Log("replay models: " + e); status("The game's models could not be shown: the replay is shown as a diagram."); Clear(); }
                    view.InvalidateVisual();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        sealed class Loaded { public Fighter[] F; public RvModel Stage; public string StageDir; public Dictionary<string, BitmapSource> Tex = new Dictionary<string, BitmapSource>(); }

        static BitmapSource Png(string f)
        {
            var b = new BitmapImage();
            b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(f); b.EndInit(); b.Freeze();
            return b;
        }

        static Loaded Read(string manifest)
        {
            var res = new Loaded();
            var slots = new List<Fighter>();
            foreach (var l in File.ReadAllLines(manifest))
            {
                var p = l.Split(' ');
                if (p[0] == "stage") { res.StageDir = Path.Combine(AssetsDir, p[1]); res.Stage = RvModel.Read(res.StageDir); foreach (var t in Textures(res.Stage, res.StageDir)) res.Tex[t.Key] = t.Value; }
                else if (p[0] == "fighter")
                {
                    int k = int.Parse(p[1]);
                    while (slots.Count <= k) slots.Add(null);
                    if (p.Length < 5) continue;
                    string dir = Path.Combine(AssetsDir, p[2]), motions = Path.Combine(AssetsDir, p[3]);
                    if (!File.Exists(Path.Combine(dir, "model.rvm"))) continue;
                    var f = new Fighter { Model = RvModel.Read(dir), Folder = dir, Scale = double.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture) };
                    foreach (var t in Textures(f.Model, dir)) res.Tex[t.Key] = t.Value;
                    foreach (var file in Directory.GetFiles(motions, "*.rva"))
                    {
                        var m = RvMotion.Read(file);
                        if (m != null) f.Motions[Convert.ToUInt64(Path.GetFileNameWithoutExtension(file), 16)] = m;
                    }
                    slots[k] = f;
                }
            }
            res.F = slots.ToArray();
            return res;
        }

        static IEnumerable<KeyValuePair<string, BitmapSource>> Textures(RvModel m, string dir)
        {
            for (int i = 0; i < m.Tex.Count; i++)
            {
                string f = Path.Combine(dir, "tex" + i + ".png");
                if (File.Exists(f)) yield return new KeyValuePair<string, BitmapSource>(f, Png(f));
            }
        }

        static Material Mat(BitmapSource tex)
        {
            Brush b = tex != null ? new ImageBrush(tex) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill }
                                  : (Brush)new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
            b.Freeze();
            var m = new DiffuseMaterial(b); m.Freeze();
            return m;
        }

        void Build(Loaded l)
        {
            root.Children.Clear();
            root.Children.Add(new AmbientLight(Color.FromRgb(0x96, 0x96, 0x9C)));
            root.Children.Add(new DirectionalLight(Color.FromRgb(0x8C, 0x8A, 0x84), new Vector3D(-.35, -.6, -.75)));
            root.Children.Add(new DirectionalLight(Color.FromRgb(0x30, 0x32, 0x38), new Vector3D(.5, .2, -.6)));
            stageOn = false;
            if (l.Stage != null)
            {
                var g = new Model3DGroup();
                string mv = Path.Combine(l.StageDir, "moving.txt");
                stageLines = File.Exists(Path.Combine(l.StageDir, "lines.txt"));
                if (File.Exists(mv))
                {
                    var q = File.ReadAllText(mv).Split(' ');
                    moveFirst = int.Parse(q[0]); moveCount = int.Parse(q[1]);
                    moveX = double.Parse(q[2], System.Globalization.CultureInfo.InvariantCulture); moveY = double.Parse(q[3], System.Globalization.CultureInfo.InvariantCulture);
                    moveBy = new TranslateTransform3D(); moveGroup = new Model3DGroup { Transform = moveBy };
                    g.Children.Add(moveGroup);
                }
                int mi = -1;
                foreach (var m in l.Stage.Meshes)
                {
                    mi++;
                    var geo = new MeshGeometry3D { TriangleIndices = m.Idx, TextureCoordinates = m.Uv };
                    int nv = m.Pos.Length / 3;
                    var pos = new Point3DCollection(nv); var nor = new Vector3DCollection(nv);
                    for (int i = 0; i < nv; i++) { pos.Add(new Point3D(m.Pos[i * 3], m.Pos[i * 3 + 1], m.Pos[i * 3 + 2])); nor.Add(new Vector3D(m.Nor[i * 3], m.Nor[i * 3 + 1], m.Nor[i * 3 + 2])); }
                    pos.Freeze(); nor.Freeze();
                    geo.Positions = pos; geo.Normals = nor; geo.Freeze();
                    var mat = Mat(TexOf(l, l.StageDir, m.Tex));
                    (mi >= moveFirst && moveFirst >= 0 && mi < moveFirst + moveCount ? moveGroup : g).Children.Add(new GeometryModel3D(geo, mat) { BackMaterial = mat });
                }
                root.Children.Add(g);
                stageOn = true;
            }
            foreach (var f in l.F)
            {
                if (f == null) continue;
                f.Group = new Model3DGroup();
                foreach (var m in f.Model.Meshes)
                {
                    m.Geo = new MeshGeometry3D { TriangleIndices = m.Idx, TextureCoordinates = m.Uv };
                    var mat = Mat(TexOf(l, f.Folder, m.Tex));
                    m.Model = new GeometryModel3D { Material = mat, BackMaterial = mat };
                    f.Group.Children.Add(m.Model);
                }
                f.World = new float[f.Model.Bones.Length * 12]; f.Skin = new float[f.Model.Bones.Length * 12];
                root.Children.Add(f.Group);
            }
            fighters = l.F;
        }

        static BitmapSource TexOf(Loaded l, string dir, int i)
        {
            if (i < 0) return null;
            return l.Tex.TryGetValue(Path.Combine(dir, "tex" + i + ".png"), out var t) ? t : null;
        }

        /// <summary>this frame, with the view's scale (pixels per game unit) and origin</summary>
        public void Update(ReplayState r, RvFrame fr, double W, double H, double sc, double ox, double oy)
        {
            bool on = On && (stageOn || fighters.Any(x => x != null));
            view.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            if (!on) return;
            cam.Width = W / sc;
            cam.Position = new Point3D((W / 2 - ox) / sc, (oy - H / 2) / sc, 3000);
            if (moveGroup != null)
            {
                // the recorded platform lines of this frame: the platform's top on them
                double x0 = double.MaxValue, x1 = double.MinValue, y = double.MinValue;
                for (int i = 0; i < r.Lines.Count; i++)
                {
                    var L = r.Lines[i];
                    if (!L.Passable || fr.Number < L.FirstFrame || (i < 64 && (fr.LinesOn & (1UL << i)) == 0)) continue;
                    var p = fr.Lines != null && i < fr.Lines.Length ? fr.Lines[i] : new[] { L.X1, L.Y1, L.X2, L.Y2 };
                    x0 = Math.Min(x0, Math.Min(p[0], p[2])); x1 = Math.Max(x1, Math.Max(p[0], p[2])); y = Math.Max(y, Math.Max(p[1], p[3]));
                }
                bool any = x0 != double.MaxValue;
                moveBy.OffsetX = any ? (x0 + x1) / 2 - moveX : 0; moveBy.OffsetY = any ? y - moveY : -100000;
            }
            for (int k = 0; k < fighters.Length; k++)
            {
                var f = fighters[k];
                if (f == null) continue;
                var s = k < fr.F.Length ? fr.F[k] : null;
                if (s == null || !f.Motions.TryGetValue(s.Motion & 0xffffffffffUL, out var motion)) { foreach (var m in f.Model.Meshes) m.Model.Geometry = null; f.Seen = -1; continue; }
                Pose(f, s, motion);
            }
        }

        // matrices: 3 x 4 by rows in the file's convention (a point is multiplied from the left: p' = p * M, translation in the last row)
        static void Mul(float[] a, int ai, float[] b, int bi, float[] o, int oi)
        {
            for (int r = 0; r < 4; r++)
            {
                float x = a[ai + r * 3], y = a[ai + r * 3 + 1], z = a[ai + r * 3 + 2];
                float tx = r == 3 ? b[bi + 9] : 0, ty = r == 3 ? b[bi + 10] : 0, tz = r == 3 ? b[bi + 11] : 0;
                o[oi + r * 3] = x * b[bi] + y * b[bi + 3] + z * b[bi + 6] + tx;
                o[oi + r * 3 + 1] = x * b[bi + 1] + y * b[bi + 4] + z * b[bi + 7] + ty;
                o[oi + r * 3 + 2] = x * b[bi + 2] + y * b[bi + 5] + z * b[bi + 8] + tz;
            }
        }
        static void FromSqt(float[] t, int i, float[] o, int oi, bool noTravel)
        {
            float sx = t[i], sy = t[i + 1], sz = t[i + 2], x = t[i + 3], y = t[i + 4], z = t[i + 5], w = t[i + 6];
            o[oi] = (1 - 2 * (y * y + z * z)) * sx; o[oi + 1] = 2 * (x * y + z * w) * sx; o[oi + 2] = 2 * (x * z - y * w) * sx;
            o[oi + 3] = 2 * (x * y - z * w) * sy; o[oi + 4] = (1 - 2 * (x * x + z * z)) * sy; o[oi + 5] = 2 * (y * z + x * w) * sy;
            o[oi + 6] = 2 * (x * z + y * w) * sz; o[oi + 7] = 2 * (y * z - x * w) * sz; o[oi + 8] = (1 - 2 * (x * x + y * y)) * sz;
            o[oi + 9] = noTravel ? 0 : t[i + 7]; o[oi + 10] = noTravel ? 0 : t[i + 8]; o[oi + 11] = noTravel ? 0 : t[i + 9];
        }
        static void From16(float[] m, float[] o, int oi)
        {
            o[oi] = m[0]; o[oi + 1] = m[1]; o[oi + 2] = m[2]; o[oi + 3] = m[4]; o[oi + 4] = m[5]; o[oi + 5] = m[6];
            o[oi + 6] = m[8]; o[oi + 7] = m[9]; o[oi + 8] = m[10]; o[oi + 9] = m[12]; o[oi + 10] = m[13]; o[oi + 11] = m[14];
        }
        readonly float[] tmp = new float[12], inv = new float[12];

        void Pose(Fighter f, RvFighterFrame s, RvMotion motion)
        {
            var bones = f.Model.Bones;
            int mf = (int)s.MotionFrame;
            var done = new bool[bones.Length];
            Action<int> world = null;
            world = i =>
            {
                if (done[i]) return;
                var b = bones[i];
                if (b.Parent >= 0) world(b.Parent);
                if (motion.Tracks.TryGetValue(i, out var tr))
                {
                    int n = tr.Length / 10, fi = mf >= n ? n - 1 : mf < 0 ? 0 : mf;
                    FromSqt(tr, fi * 10, tmp, 0, b.Parent < 0);
                }
                else From16(b.Local, tmp, 0);
                if (b.Parent >= 0) Mul(tmp, 0, f.World, b.Parent * 12, f.World, i * 12);
                else Array.Copy(tmp, 0, f.World, i * 12, 12);
                done[i] = true;
            };
            for (int i = 0; i < bones.Length; i++) world(i);
            for (int i = 0; i < bones.Length; i++) { From16(bones[i].Inv, inv, 0); Mul(inv, 0, f.World, i * 12, f.Skin, i * 12); }
            float k = (float)f.Scale, lr = s.Lr, X = s.X, Y = s.Y;
            var skin = f.Skin;
            foreach (var m in f.Model.Meshes)
            {
                bool vis = m.Default;
                if (m.Vis >= 0 && motion.Vis.TryGetValue(f.Model.Vis[m.Vis], out var vt) && vt.Length > 0) vis = vt[mf >= vt.Length ? vt.Length - 1 : mf < 0 ? 0 : mf] != 0;
                if (!vis) { m.Model.Geometry = null; continue; }
                int nv = m.Pos.Length / 3;
                var pos = new Point3DCollection(nv); var nor = new Vector3DCollection(nv);
                float[] P = m.Pos, N = m.Nor, W = m.SkinW; byte[] B = m.SkinB;
                for (int v = 0; v < nv; v++)
                {
                    float px = P[v * 3], py = P[v * 3 + 1], pz = P[v * 3 + 2], nx = N[v * 3], ny = N[v * 3 + 1], nz = N[v * 3 + 2];
                    float ox = 0, oy = 0, oz = 0, ax = 0, ay = 0, az = 0;
                    for (int j = 0; j < 4; j++)
                    {
                        float w = W[v * 4 + j];
                        if (w <= 0) continue;
                        int b = B[v * 4 + j] * 12;
                        ox += w * (px * skin[b] + py * skin[b + 3] + pz * skin[b + 6] + skin[b + 9]);
                        oy += w * (px * skin[b + 1] + py * skin[b + 4] + pz * skin[b + 7] + skin[b + 10]);
                        oz += w * (px * skin[b + 2] + py * skin[b + 5] + pz * skin[b + 8] + skin[b + 11]);
                        ax += w * (nx * skin[b] + ny * skin[b + 3] + nz * skin[b + 6]);
                        ay += w * (nx * skin[b + 1] + ny * skin[b + 4] + nz * skin[b + 7]);
                        az += w * (nx * skin[b + 2] + ny * skin[b + 5] + nz * skin[b + 8]);
                    }
                    // the fighter turned to its side: its front (+z) along its facing
                    pos.Add(new Point3D(X + k * oz * lr, Y + k * oy, -k * ox * lr));
                    nor.Add(new Vector3D(az * lr, ay, -ax * lr));
                }
                pos.Freeze(); nor.Freeze();
                m.Geo = new MeshGeometry3D { TriangleIndices = m.Idx, TextureCoordinates = m.Uv, Positions = pos, Normals = nor };
                m.Geo.Freeze();
                m.Model.Geometry = m.Geo;
            }
        }
    }
}
