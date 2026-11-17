using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WorldPerformance
{
    // While the hammer is out, ComfortMe 1.3.1 redraws its "This room" panel twice a frame, from its Hud.Update and
    // Hud.SetupPieceInfo postfixes, and the comfort chip on the selected piece's icon once a frame. Each panel redraw
    // localizes and joins every line again and resets the panel's size, which makes its layout rebuild, and each chip redraw
    // classifies the piece and searches the icon's children for its chip. What they show depends only on ComfortMe's comfort
    // snapshot, a new object whenever ComfortMe takes it again (at most five times a second, or when the player moves or
    // shelter changes), on the piece, on ComfortMe's settings and language, and on the panel and chip as the last redraw left
    // them. While all of those are unchanged the redraw is skipped, so the panel and chip stay exactly as drawn; anything else,
    // such as the build menu hiding the chip, a new language or a changed setting, redraws natively. With comfort links on,
    // the panel's reset size decides whether hovering it shows them, and with chips limited to affordable or best pieces the
    // chip reads the inventory, so those settings keep the native redraws.
    internal static class ComfortPanelRefresh
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".comfort-panel-refresh");
        private static readonly Vector2 Dock = new Vector2(0.84f, 0.78f), Pivot = new Vector2(0f, 1f);
        private const float Width = 320f;
        private static AccessTools.FieldRef<Image> panel;
        private static AccessTools.FieldRef<TextMeshProUGUI> header, body;
        private static AccessTools.FieldRef<Canvas> overlay;
        private static AccessTools.FieldRef<string> lastText;
        private static AccessTools.FieldRef<TMP_FontAsset> font;
        private static Func<bool> holdingPlaceTool;
        private static Action ensureLanguage;
        private static Func<Transform, Transform> findChip;
        private static ConfigEntry<bool> enabled, showPanel, showLinks, showChip, upgradesOnly, requireMaterials;
        private static ConfigEntryBase policy;
        private static ConfigFile settings;
        private static int settingsVersion;
        private static bool chipSettingsStable;

        // What the last native panel redraw saw and left.
        private static Hud drawnHud;
        private static object drawnSnapshot;
        private static string drawnText;
        private static Image drawnPanel;
        private static Canvas drawnOverlay;

        // What the last native chip redraw saw and left.
        private static Image chipIcon;
        private static Piece chipPiece;
        private static object chipSnapshot;
        private static Player chipPlayer;
        private static Hud chipHud;
        private static int chipSettings, chipChildren;
        private static Transform chip;
        private static Image chipBackground;
        private static TextMeshProUGUI chipLabel;
        private static bool chipShown;
        private static string chipText;
        private static Color chipColor;

        internal static bool Running { get; private set; }
        // Off redraws every frame, as natively; the isolated client switches it for checks and paired measurements.
        internal static bool Skipping = true;
        internal static long PanelSkipped, PanelDrawn, ChipSkipped, ChipDrawn;

        internal static void Start()
        {
            if (Running) return;
            if (!PluginIds.TryGet(PluginIds.ComfortMe, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            Type Find(string name) => assembly.GetType(name) ?? throw new MissingMemberException("ComfortMe's " + name + " changed.");
            FieldInfo Field(Type type, string name, Type fieldType)
            {
                var field = AccessTools.Field(type, name);
                return field != null && field.IsStatic && field.FieldType == fieldType ? field : throw new MissingMemberException("ComfortMe's " + type.Name + "." + name + " changed.");
            }
            MethodInfo Method(Type type, string name, Type[] parameters, Type result)
            {
                var method = AccessTools.Method(type, name, parameters);
                return method != null && method.IsStatic && method.ReturnType == result ? method : throw new MissingMethodException("ComfortMe's " + type.Name + "." + name + " changed.");
            }
            var catalog = Find("ComfortMe.GroupCatalogPanel");
            var badge = Find("ComfortMe.BuildHudBadge");
            var snapshot = Find("ComfortMe.ComfortSnapshot");
            var hudUi = Find("ComfortMe.HudUi");
            var config = Find("ComfortMe.ModConfig");
            panel = AccessTools.StaticFieldRefAccess<Image>(Field(catalog, "panel", typeof(Image)));
            header = AccessTools.StaticFieldRefAccess<TextMeshProUGUI>(Field(catalog, "header", typeof(TextMeshProUGUI)));
            body = AccessTools.StaticFieldRefAccess<TextMeshProUGUI>(Field(catalog, "body", typeof(TextMeshProUGUI)));
            overlay = AccessTools.StaticFieldRefAccess<Canvas>(Field(catalog, "overlay", typeof(Canvas)));
            lastText = AccessTools.StaticFieldRefAccess<string>(Field(catalog, "lastText", typeof(string)));
            font = AccessTools.StaticFieldRefAccess<TMP_FontAsset>(Field(hudUi, "cachedFont", typeof(TMP_FontAsset)));
            holdingPlaceTool = AccessTools.MethodDelegate<Func<bool>>(Method(hudUi, "HoldingPlaceTool", Type.EmptyTypes, typeof(bool)));
            ensureLanguage = AccessTools.MethodDelegate<Action>(Method(Find("ComfortMe.ModLocalization"), "EnsureApplied", Type.EmptyTypes, typeof(void)));
            findChip = AccessTools.MethodDelegate<Func<Transform, Transform>>(Method(badge, "FindBadge", new[] { typeof(Transform) }, typeof(Transform)));
            ConfigEntry<bool> Setting(string name) => (ConfigEntry<bool>)Field(config, name, typeof(ConfigEntry<bool>)).GetValue(null) ?? throw new MissingMemberException("ComfortMe's " + name + " setting is not bound.");
            enabled = Setting("Enabled"); showPanel = Setting("ShowGroupCatalog"); showLinks = Setting("ShowComfortLinks");
            showChip = Setting("ShowValueBadge"); upgradesOnly = Setting("BadgeUpgradesOnly"); requireMaterials = Setting("RequireMaterials");
            policy = AccessTools.Field(config, "Policy")?.GetValue(null) as ConfigEntryBase ?? throw new MissingMemberException("ComfortMe's HighlightPolicy setting changed.");
            var refresh = Method(catalog, "Refresh", new[] { typeof(Hud), snapshot }, typeof(void));
            var apply = Method(badge, "ApplyToImage", new[] { typeof(Image), typeof(Piece), snapshot }, typeof(void));
            settings = plugin.Instance.Config;
            settings.SettingChanged += SettingChanged;
            SettingChanged(null, null);
            try
            {
                patches.Patch(refresh, prefix: new HarmonyMethod(typeof(ComfortPanelRefresh), nameof(Redrawing)), postfix: new HarmonyMethod(typeof(ComfortPanelRefresh), nameof(Redrawn)));
                patches.Patch(apply, prefix: new HarmonyMethod(typeof(ComfortPanelRefresh), nameof(Marking)), postfix: new HarmonyMethod(typeof(ComfortPanelRefresh), nameof(Marked)));
            }
            catch
            {
                patches.UnpatchSelf();
                settings.SettingChanged -= SettingChanged;
                throw;
            }
            Running = true;
            Plugin.Log.LogInfo("ComfortMe's This room panel and comfort chip redraw only when they change.");
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            settings.SettingChanged -= SettingChanged;
            drawnSnapshot = chipSnapshot = null;
        }

        internal static string Report() => $"panel redraws skipped {PanelSkipped}, drawn {PanelDrawn}; chip redraws skipped {ChipSkipped}, drawn {ChipDrawn}";

        // Any change of a ComfortMe setting redraws the chip natively once, and decides whether the chip can read the inventory.
        private static void SettingChanged(object sender, EventArgs args)
        {
            settingsVersion++;
            chipSettingsStable = !showChip.Value || (!requireMaterials.Value && (!upgradesOnly.Value || policy.BoxedValue.ToString() != "BestAvailable"));
        }

        private static bool Exact(Vector2 first, Vector2 second) => first.x == second.x && first.y == second.y;
        private static bool Exact(Color first, Color second) => first.r == second.r && first.g == second.g && first.b == second.b && first.a == second.a;

        private static bool Redrawing(Hud __0, object __1, out bool __state)
        {
            __state = !Skipping || !PanelUnchanged(__0, __1);
            if (__state) { drawnSnapshot = null; return true; }
            PanelSkipped++;
            return false;
        }

        // The native redraw's own conditions, then everything it reads and writes as it left them.
        private static bool PanelUnchanged(Hud hud, object snapshot)
        {
            if (hud == null || snapshot == null || !ReferenceEquals(snapshot, drawnSnapshot) || hud != drawnHud || drawnText == null) return false;
            if (!enabled.Value || !showPanel.Value || showLinks.Value || !holdingPlaceTool()) return false;
            // A new language or downloaded translations clear the drawn text.
            ensureLanguage();
            if (!ReferenceEquals(lastText(), drawnText)) return false;
            var image = panel();
            var canvas = overlay();
            var top = header();
            var lines = body();
            var face = font();
            if (!image || image != drawnPanel || !canvas || canvas != drawnOverlay || !top || !lines || !face) return false;
            if (!canvas.gameObject.activeSelf || !image.gameObject.activeSelf || !lines.gameObject.activeSelf || top.font != face || lines.font != face) return false;
            var rect = image.rectTransform;
            var parent = canvas.transform;
            return rect.parent == parent && rect.GetSiblingIndex() == parent.childCount - 1 && Exact(rect.pivot, Pivot) && Exact(rect.anchorMin, Dock) &&
                Exact(rect.anchorMax, Dock) && Exact(rect.anchoredPosition, Vector2.zero) && rect.sizeDelta.x == Width;
        }

        private static void Redrawn(Hud __0, object __1, bool __state)
        {
            if (!__state) return;
            PanelDrawn++;
            drawnHud = __0;
            drawnSnapshot = __1;
            drawnText = lastText();
            drawnPanel = panel();
            drawnOverlay = overlay();
        }

        private static bool Marking(Image __0, Piece __1, object __2, out bool __state)
        {
            __state = !Skipping || !ChipUnchanged(__0, __1, __2);
            if (__state) { chipSnapshot = null; return true; }
            ChipSkipped++;
            return false;
        }

        private static bool ChipUnchanged(Image icon, Piece piece, object snapshot)
        {
            if (!chipSettingsStable || settingsVersion != chipSettings || icon == null || icon != chipIcon || snapshot == null || !ReferenceEquals(snapshot, chipSnapshot)) return false;
            // A destroyed piece classifies as none, so it redraws.
            if (!ReferenceEquals(piece, chipPiece) || (piece is object && !piece) || Player.m_localPlayer != chipPlayer || Hud.instance != chipHud) return false;
            if (chip is null) return icon.transform.childCount == chipChildren;
            if (!chip || chip.parent != icon.transform || chip.gameObject.activeSelf != chipShown) return false;
            if (!chipShown) return true;
            return chip.GetSiblingIndex() == icon.transform.childCount - 1 && chipBackground && chipBackground.maskable && chipLabel && chipLabel.maskable &&
                chipLabel.font == font() && chipLabel.text == chipText && Exact(chipLabel.color, chipColor);
        }

        private static void Marked(Image __0, Piece __1, object __2, bool __state)
        {
            if (!__state) return;
            ChipDrawn++;
            if (__0 == null) return;
            chip = findChip(__0.transform);
            chipShown = chip && chip.gameObject.activeSelf;
            chipBackground = chip ? chip.GetComponent<Image>() : null;
            chipLabel = chip ? chip.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            // A shown chip without its background or label is redrawn natively.
            if (chipShown && (!chipBackground || !chipLabel)) return;
            chipText = chipLabel ? chipLabel.text : null;
            chipColor = chipLabel ? chipLabel.color : default;
            chipChildren = __0.transform.childCount;
            chipIcon = __0;
            chipPiece = __1;
            chipPlayer = Player.m_localPlayer;
            chipHud = Hud.instance;
            chipSettings = settingsVersion;
            chipSnapshot = __2;
        }
    }
}
