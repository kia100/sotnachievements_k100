using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Modding;
using Recompiled;
using Sotn;

namespace SotnAchievements;

// one achievement json gives us the metadata, the rest gets filled in as we go
public sealed class Achievement
{
    public string Id = "";
    public string Title = "";
    public string Desc = "";
    public string Badge = "";
    public Func<bool> Earned = () => false; // my own check, unlocks retroactively
    public Cheevos.Trigger? Trig; // raw RA logic, for live detection

    public bool Unlocked;

    // loaded lazily, has to be from the render thread
    public uint TexColor;
    public uint TexGray;
    public int WorldImage = -1;
    public bool WorldTried;
}

public sealed class AchievementsMod : IMod
{
    public const string ModId = "sotnachievements";
    const string Prefix = "mods.sotnachievements.unlocked.";

    public const double BannerSeconds = 4.5;

    // RA addresses are raw psx ram offsets, recomp keeps ram at 0x80000000 so just add that on
    const uint RamBase = 0x80000000;
    static byte RaRead(uint a) => Runtime.Mem!.ReadU8(RamBase + a);

    // little placement knobs, nudge these if it looks off in game
    const int HeadOffset = 20;
    const int WorldOrder = 8;
    public const int RibbonYOffset = 0;

    // stuff the tilemap render path leaves around, same trick as the ThroneLeftFill patch
    const uint CurrentBufferPtr = 0x8006C37C;
    const uint BackbufferX = 0x8006C39C;
    const uint BackbufferY = 0x8006C3A0;
    const uint TilemapAddr = 0x80073084;
    const uint OtOfs = 0x474;
    const int OtSize = 0x200;
    const int DefaultBadgeSize = 64;

    // for the odd cheevo the little json dsl cant do, drop a c# check in here keyed by id
    static readonly Dictionary<string, Func<bool>> Overrides = new()
    {
        // e.g. { "richters_bane", () => Player.Level >= 99 },
    };

    public static AchievementsMod? Instance;
    static bool _uiRegistered;

    public readonly List<Achievement> Achievements = new();

    string? _assetsDir;
    readonly Queue<Achievement> _pending = new();
    Achievement? _current;
    DateTime _bannerStart;

    public Achievement? CurrentBanner => _current;
    public double BannerElapsed => (DateTime.UtcNow - _bannerStart).TotalSeconds;

    public void OnLoad()
    {
        Instance = this;

        string? root = ModLoader.LoadedMods.FirstOrDefault(m => m.Id == ModId)?.SourcePath;
        _assetsDir = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "assets");

        LoadDefinitions();

        foreach (var a in Achievements)
            a.Unlocked = Runtime.View.GetBool(Prefix + a.Id, false);

        Event.AddListener<VSyncEvent>(OnVSync);
        Event.AddListener<TilemapRenderedEvent>(OnTilemapRendered);

        if (!_uiRegistered)
        {
            _uiRegistered = true;
            PanelManager.Register(new AchievementsPanel());
            PanelManager.Register(new BannerPanel());
            MenuRegistry.Menu("Achievements", MenuRegistry.OrderGame + 50)
                .Panel<AchievementsPanel>("Achievements");
        }
    }

    public void OnUnload()
    {
        Event.RemoveListener<VSyncEvent>(OnVSync);
        Event.RemoveListener<TilemapRenderedEvent>(OnTilemapRendered);
        _pending.Clear();
        _current = null;
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    void LoadDefinitions()
    {
        Achievements.Clear();
        if (_assetsDir == null) return;
        string path = Path.Combine(_assetsDir, "achievements.json");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[Achievements] {path} not found");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string id = Str(e, "id");
                if (string.IsNullOrEmpty(id)) continue;
                string memaddr = Str(e, "memaddr");
                Cheevos.Trigger? trig = null;
                if (memaddr.Length > 0)
                {
                    try { trig = Cheevos.Trigger.Parse(memaddr); }
                    catch (Exception ex) { Console.Error.WriteLine($"[Achievements] {id}: bad memaddr ({ex.Message})"); }
                }
                Achievements.Add(new Achievement
                {
                    Id = id,
                    Badge = Str(e, "badge"),
                    Title = Str(e, "title"),
                    Desc = Str(e, "desc"),
                    Earned = ResolveCondition(id, Str(e, "condition")),
                    Trig = trig,
                });
            }
            Console.WriteLine($"[Achievements] loaded {Achievements.Count} achievement(s)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Achievements] failed to read achievements.json: {ex.Message}");
        }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // my tiny condition dsl. these all read save-backed state so they unlock live AND retroactively,
    // which the raw RA logic mostly can't (it's edge triggered). atoms:
    //   timeattack:N[,M...]  boss slot(s) beaten   level:N  alucard hit level N
    //   relic:/hand:/body:Name[,...]  owned (enum names)     warpsall  all first-castle warps
    //   always  (debug)
    // chain atoms with ';' for AND. empty/unknown -> just never fires, cheevo stays locked.
    static Func<bool> ResolveCondition(string id, string spec)
    {
        if (Overrides.TryGetValue(id, out var f)) return f;
        spec = spec.Trim();
        if (spec.Length == 0) return () => false;

        var parts = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(p => ParseAtom(id, p)).ToArray();
        return parts.Length == 1 ? parts[0] : () => parts.All(p => p());
    }

    static Func<bool> ParseAtom(string id, string atom)
    {
        int colon = atom.IndexOf(':');
        string kind = colon < 0 ? atom : atom[..colon];
        string arg = colon < 0 ? "" : atom[(colon + 1)..];

        switch (kind)
        {
            case "always":
                return () => true;

            case "timeattack": // boss slot(s) in the time-attack table, all have to be beaten
            {
                var slots = Csv(arg).Where(x => int.TryParse(x, out _)).Select(int.Parse).ToArray();
                if (slots.Length > 0)
                    return () => slots.All(s => Progress.GetTimeAttack((TimeAttackEvent)s) != 0);
                break;
            }

            case "level": // richter never levels so make sure it's actually alucard we're looking at
                if (int.TryParse(arg, out int lvl))
                    return () => Player.IsAlucard && Player.Level >= lvl;
                break;

            case "relic":
            {
                var relics = Names<Relic>(arg);
                if (relics.Length > 0)
                    return () => relics.All(Inventory.HasRelic);
                break;
            }

            case "hand":
            {
                var items = Names<HandItem>(arg);
                if (items.Length > 0)
                    return () => items.All(i => Inventory.OwnsHandItem((int)i));
                break;
            }

            case "body":
            {
                var items = Names<BodyItem>(arg);
                if (items.Length > 0)
                    return () => items.All(i => Inventory.OwnsBodyItem((int)i));
                break;
            }

            case "warpsall": // all five first-castle warp rooms lit
                return () => Progress.WarpsFirstCastle ==
                    (Warp.Entrance | Warp.Mines | Warp.OuterWall | Warp.Keep | Warp.Olrox);
        }

        Console.Error.WriteLine($"[Achievements] {id}: unhandled condition \"{atom}\"; will not auto-unlock");
        return () => false;
    }

    static string[] Csv(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // enum names -> values. a name that doesn't parse just gets dropped, so a typo in the json means
    // that one quietly never unlocks instead of throwing. beats crashing the whole mod over a comma
    static T[] Names<T>(string csv) where T : struct, Enum =>
        Csv(csv).Where(s => Enum.TryParse<T>(s, out _)).Select(s => Enum.Parse<T>(s)).ToArray();

    public void DrawSettings()
    {
        var mod = Instance ?? this;
        int unlocked = mod.Achievements.Count(a => a.Unlocked);
        ImGui.Text($"{unlocked} / {mod.Achievements.Count} unlocked");
        ImGui.TextDisabled("Open the \"Achievements\" menu to view the full list.");
        ImGui.Separator();
        if (ImGui.Button("Reset all achievements"))
        {
            foreach (var a in mod.Achievements)
            {
                a.Unlocked = false;
                Runtime.View.SetBool(Prefix + a.Id, false);
            }
            Runtime.SaveView();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(they re-unlock next time their condition is met)");
    }

    void OnVSync(VSyncEvent e)
    {
        TickBanner();

        // the RA triggers track deltas + hit counts frame by frame, so we have to poll every frame
        // and can't get clever about skipping. bail early instead when there's nothing to read.
        if (!Game.Available || !Game.InGame) return;

        foreach (var a in Achievements)
        {
            if (a.Unlocked) continue;

            bool earned = false;
            try { earned = a.Earned(); } catch { } // my check first, this is the one that catches old saves
            if (!earned && a.Trig != null)
                try { earned = a.Trig.Test(RaRead); } catch { } // otherwise fall back to the real RA logic

            if (earned) Unlock(a);
        }
    }

    void TickBanner()
    {
        if (_current != null && BannerElapsed > BannerSeconds) _current = null;
        if (_current == null && _pending.Count > 0)
        {
            _current = _pending.Dequeue();
            _bannerStart = DateTime.UtcNow;
        }
    }

    void Unlock(Achievement a)
    {
        a.Unlocked = true;
        Runtime.View.SetBool(Prefix + a.Id, true);
        Runtime.SaveView();
        _pending.Enqueue(a);
        Console.WriteLine($"[Achievements] unlocked: {a.Title}");
    }

    // the badge that pops up over alucards head, drawn straight into the game with GpuPrims
    void OnTilemapRendered(TilemapRenderedEvent e)
    {
        var a = _current;
        if (a == null) return;

        var m = e.Memory;
        if (m == null || !Game.InGame) return;

        int img = EnsureWorldImage(a, out int dim);
        if (img < 0) return;

        var p = Entities.Player;
        if (!p.IsAlive) return;

        // same guard ThroneLeftFill uses: if the tilemap isn't up this frame the sprite lands in the
        // void and flickers, so just skip
        if (m.ReadU32(TilemapAddr + 0x20) == 0 || m.ReadU32(TilemapAddr + 0x00) == 0) return;

        GpuPrims.SetOrderingTable(m.ReadU32(CurrentBufferPtr) + OtOfs, OtSize);

        const int drawSize = 32;
        double t = BannerElapsed;
        int rise = (int)(Math.Min(t, 0.4) / 0.4 * 8); // small pop upward

        int bx = (int)m.ReadU32(BackbufferX);
        int by = (int)m.ReadU32(BackbufferY);
        int x = bx + (p.PosX - e.ScrollX) - drawSize / 2;
        int y = by + (p.PosY - e.ScrollY) - HeadOffset - drawSize - rise;

        GpuPrims.Sprite(WorldOrder, img, x, y, drawSize, drawSize);
    }

    int EnsureWorldImage(Achievement a, out int dim)
    {
        dim = DefaultBadgeSize;
        if (a.WorldImage >= 0) return a.WorldImage;
        if (a.WorldTried || _assetsDir == null) return -1;
        a.WorldTried = true;
        try
        {
            // we ship pre-decoded rgba blobs on purpose so the mod doesn't need an image lib just to
            // draw one badge in-world. todo: decode the png here and drop the .rgba files if it's ever worth it
            byte[] rgba = File.ReadAllBytes(Path.Combine(_assetsDir, a.Badge + ".rgba"));
            int side = (int)Math.Round(Math.Sqrt(rgba.Length / 4.0)); // badges are square, so get the size back from the length
            if (side * side * 4 != rgba.Length) side = DefaultBadgeSize;
            dim = side;
            a.WorldImage = GpuPrims.RegisterImage(rgba, side, side);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Achievements] world icon load failed for {a.Id}: {ex.Message}");
            a.WorldImage = -1;
        }
        return a.WorldImage;
    }

    // grab the imgui texture for a badge (color one or the locked one). render thread only!
    public uint TexFor(Achievement a, bool unlocked)
    {
        if (_assetsDir == null) return 0;

        // already uploaded it? hand it back
        if (unlocked && a.TexColor != 0) return a.TexColor;
        if (!unlocked && a.TexGray != 0) return a.TexGray;

        uint tex = 0;
        string file = unlocked ? a.Badge + ".png" : a.Badge + "_lock.png";
        try { tex = HostWindow.UploadPng(File.ReadAllBytes(Path.Combine(_assetsDir, file))); }
        catch (Exception ex) { Console.Error.WriteLine($"[Achievements] icon load failed for {a.Id}: {ex.Message}"); }

        if (unlocked) a.TexColor = tex;
        else a.TexGray = tex;
        return tex;
    }

    public static uint Col(byte r, byte g, byte b, byte a) =>
        (uint)(r | (g << 8) | (b << 16) | (a << 24));
}

// the "Achievements" window you open from the menu bar - just the full list
public sealed class AchievementsPanel : IPanel
{
    public string Name => "Achievements";
    public string TitleKey => "Achievements";
    public bool IsOpen { get; set; }

    public void Draw()
    {
        var mod = AchievementsMod.Instance;

        ImGui.SetNextWindowSize(new Vector2(460, 420), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin("Achievements", ref open))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }

        if (mod == null)
        {
            ImGui.TextDisabled("Not loaded.");
            IsOpen = open;
            ImGui.End();
            return;
        }

        int unlocked = mod.Achievements.Count(a => a.Unlocked);
        ImGui.Text($"{unlocked} / {mod.Achievements.Count} unlocked");
        ImGui.Separator();

        ImGui.BeginChild("##list");
        foreach (var a in mod.Achievements) DrawRow(mod, a);
        ImGui.EndChild();

        IsOpen = open;
        ImGui.End();
    }

    static void DrawRow(AchievementsMod mod, Achievement a)
    {
        var size = new Vector2(48, 48);
        uint tex = mod.TexFor(a, a.Unlocked);
        if (tex != 0)
            ImGui.Image((nint)tex, size, Vector2.Zero, Vector2.One,
                new Vector4(1, 1, 1, a.Unlocked ? 1f : 0.6f));
        else
            ImGui.Dummy(size);
        ImGui.SameLine();

        ImGui.BeginGroup();
        ImGui.TextColored(a.Unlocked ? new Vector4(1f, 0.84f, 0.35f, 1f)
                                     : new Vector4(0.6f, 0.6f, 0.6f, 1f), a.Title);
        ImGui.TextDisabled(a.Desc);
        ImGui.TextColored(a.Unlocked ? new Vector4(0.4f, 0.85f, 0.4f, 1f)
                                     : new Vector4(0.5f, 0.5f, 0.5f, 1f),
            a.Unlocked ? "Unlocked" : "Locked");
        ImGui.EndGroup();
        ImGui.Separator();
    }
}

// the unlock ribbon it's an always-on overlay dressed up to look like a sotn menu box,
// never actually shows as a real window (we only draw on the foreground list)
public sealed class BannerPanel : IPanel
{
    public string Name => "Achievement Banner";
    public string TitleKey => "Achievement Banner";
    public bool IsOpen { get => true; set { } } // always ticked so DrawPanels calls Draw() every frame

    const string Label = "ACHIEVEMENT UNLOCKED";
    const float LabelSize = 13f, TitleSize = 22f, LabelSpace = 2.5f, TitleSpace = 1.5f;

    static uint C(byte r, byte g, byte b, byte a) => AchievementsMod.Col(r, g, b, a);

    public void Draw()
    {
        var mod = AchievementsMod.Instance;
        var a = mod?.CurrentBanner;
        if (mod == null || a == null) return;

        double t = mod.BannerElapsed;
        double dur = AchievementsMod.BannerSeconds;

        float alpha = 1f;
        if (t < 0.3) alpha = (float)(t / 0.3);
        else if (t > dur - 0.6) alpha = (float)Math.Max(0, (dur - t) / 0.6);
        alpha = Math.Clamp(alpha, 0f, 1f);
        byte A = (byte)(alpha * 255);
        if (A == 0) return;

        var font = ImGui.GetFont();
        string title = a.Title.ToUpperInvariant();
        float labelW = MeasureSpaced(LabelSize, Label, LabelSpace);
        float titleW = MeasureSpaced(TitleSize, title, TitleSpace);

        const float pad = 14f, iconDim = 54f, gap = 16f, h = 88f;
        float contentW = Math.Max(labelW, titleW);
        var vp = ImGui.GetMainViewport();
        float w = Math.Clamp(pad + iconDim + gap + contentW + pad, 300f, vp.WorkSize.X - 24f);

        float slide = (float)Math.Min(1.0, t / 0.3);
        float restTop = vp.WorkPos.Y + (vp.WorkSize.Y - h) * 0.5f + AchievementsMod.RibbonYOffset;
        float x = vp.WorkPos.X + (vp.WorkSize.X - w) * 0.5f;
        float y = restTop + (1f - slide) * 16f;

        var dl = ImGui.GetForegroundDrawList();
        var p0 = new Vector2(x, y);
        var p1 = new Vector2(x + w, y + h);

        // drop shadow
        dl.AddRectFilled(p0 + new Vector2(5, 7), p1 + new Vector2(5, 7), C(0, 0, 0, (byte)(A * 0.45f)), 6f);
        byte body = (byte)(A * 0.97f);
        dl.AddRectFilledMultiColor(p0, p1,
            C(22, 24, 60, body), C(22, 24, 60, body), C(8, 8, 26, body), C(8, 8, 26, body));
        // gold double frame
        dl.AddRect(p0, p1, C(210, 186, 120, A), 5f, ImDrawFlags.None, 2.5f);
        var q0 = p0 + new Vector2(4, 4);
        var q1 = p1 - new Vector2(4, 4);
        dl.AddRect(q0, q1, C(120, 96, 52, A), 3f, ImDrawFlags.None, 1f);
        CornerTicks(dl, p0, p1, C(235, 214, 150, A));

        // framed badge
        uint tex = mod.TexFor(a, true);
        var ip0 = new Vector2(x + pad, y + (h - iconDim) * 0.5f);
        var ip1 = ip0 + new Vector2(iconDim, iconDim);
        if (tex != 0)
            dl.AddImage((nint)tex, ip0, ip1, Vector2.Zero, Vector2.One, C(255, 255, 255, A));
        else
            dl.AddRectFilled(ip0, ip1, C(40, 40, 70, A), 3f);
        dl.AddRect(ip0 - new Vector2(1, 1), ip1 + new Vector2(1, 1), C(210, 186, 120, A), 2f, ImDrawFlags.None, 1.5f);

        // text block
        float tx = ip1.X + gap;
        float blockH = LabelSize + 8f + TitleSize;
        float ty = y + (h - blockH) * 0.5f;
        DrawSpaced(font, LabelSize, new Vector2(tx, ty), C(228, 198, 110, A), Label, LabelSpace);
        DrawSpaced(font, TitleSize, new Vector2(tx, ty + LabelSize + 8f), C(246, 244, 236, A), title, TitleSpace);
    }

    static void CornerTicks(ImDrawListPtr dl, Vector2 p0, Vector2 p1, uint col)
    {
        const float s = 10f, o = 8f, th = 2f;
        dl.AddLine(new Vector2(p0.X + o, p0.Y + o), new Vector2(p0.X + o + s, p0.Y + o), col, th);
        dl.AddLine(new Vector2(p0.X + o, p0.Y + o), new Vector2(p0.X + o, p0.Y + o + s), col, th);
        dl.AddLine(new Vector2(p1.X - o, p0.Y + o), new Vector2(p1.X - o - s, p0.Y + o), col, th);
        dl.AddLine(new Vector2(p1.X - o, p0.Y + o), new Vector2(p1.X - o, p0.Y + o + s), col, th);
        dl.AddLine(new Vector2(p0.X + o, p1.Y - o), new Vector2(p0.X + o + s, p1.Y - o), col, th);
        dl.AddLine(new Vector2(p0.X + o, p1.Y - o), new Vector2(p0.X + o, p1.Y - o - s), col, th);
        dl.AddLine(new Vector2(p1.X - o, p1.Y - o), new Vector2(p1.X - o - s, p1.Y - o), col, th);
        dl.AddLine(new Vector2(p1.X - o, p1.Y - o), new Vector2(p1.X - o, p1.Y - o - s), col, th);
    }

    static float MeasureSpaced(float size, string text, float spacing)
    {
        float scale = size / ImGui.GetFontSize();
        float w = 0f;
        foreach (char ch in text) w += ImGui.CalcTextSize(ch.ToString()).X * scale + spacing;
        return text.Length > 0 ? w - spacing : 0f;
    }

    static void DrawSpaced(ImFontPtr font, float size, Vector2 pos, uint col, string text, float spacing)
    {
        float scale = size / ImGui.GetFontSize();
        float x = pos.X;
        foreach (char ch in text)
        {
            string s = ch.ToString();
            ImGui.GetForegroundDrawList().AddText(font, size, new Vector2(x, pos.Y), col, s);
            x += ImGui.CalcTextSize(s).X * scale + spacing;
        }
    }
}