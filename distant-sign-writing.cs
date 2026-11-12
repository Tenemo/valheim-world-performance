using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace WorldPerformance
{
    // A visible exception the owner approved on 2026-11-10: a sign's writing more than 60 m from the camera is not drawn,
    // while the sign board is. The game prepares and submits each sign's world-space canvas every frame, also behind walls:
    // one base's 38 signs seen from 100 m cost about 0.2 ms of camera render and 1.2-1.5 ms of render thread, and only one
    // label reached the image there, 18 pixels. Each frame, right after the game camera has moved and before the canvases
    // are prepared, a sign's canvas is switched off beyond the reach and on again within it; a canvas switched on later in
    // the frame would draw only from the next one. A sign whose text changed while its canvas was off lays its text out
    // again in the frame it comes back, so it shows its current text.
    internal static class DistantSignWriting
    {
        internal const float Reach = 60f;
        private sealed class Writing
        {
            internal Canvas Canvas;
            internal TMP_Text Text;
            // Switched off here; only those are switched on again.
            internal bool Hidden;
        }

        private static readonly Harmony patches = new Harmony(Plugin.Id + ".distant-sign-writing");
        private static readonly List<Writing> writings = new List<Writing>();
        internal static bool Running { get; private set; }
        private static bool hiding = true;
        internal static string Failure { get; private set; }

        // Off draws every sign's writing natively; the isolated client switches it for checks and paired measurements.
        internal static bool Hiding
        {
            get => hiding;
            set
            {
                if (value == hiding) return;
                hiding = value;
                if (!value) ShowAll();
                else Apply();
            }
        }

        internal static int Count => writings.Count;
        internal static int HiddenCount
        {
            get
            {
                int count = 0;
                foreach (var writing in writings) if (writing.Hidden && writing.Canvas) count++;
                return count;
            }
        }

        internal static void Start()
        {
            if (Running) return;
            patches.Patch(AccessTools.Method(typeof(Sign), "Awake") ?? throw new MissingMethodException("Sign.Awake changed."),
                postfix: new HarmonyMethod(typeof(DistantSignWriting), nameof(Register)));
            // Every camera change, including other mods' patches of the camera update, happens inside this method.
            patches.Patch(AccessTools.Method(typeof(GameCamera), "LateUpdate") ?? throw new MissingMethodException("GameCamera.LateUpdate changed."),
                postfix: new HarmonyMethod(typeof(DistantSignWriting), nameof(Apply)));
            Running = true;
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            ShowAll();
            writings.Clear();
        }

        private static void Register(Sign __instance)
        {
            var text = __instance.m_textWidget;
            var canvas = text ? text.GetComponentInParent<Canvas>(true) : null;
            if (!canvas || canvas.renderMode != RenderMode.WorldSpace) return;
            writings.Add(new Writing { Canvas = canvas, Text = text });
        }

        // The writing whose sign is the given one: whether it is registered and whether this feature has switched it off.
        internal static (bool registered, bool hidden) State(Sign sign)
        {
            foreach (var writing in writings)
                if (writing.Text && writing.Text == sign.m_textWidget) return (true, writing.Hidden);
            return (false, false);
        }

        private static void Apply()
        {
            if (!hiding || Failure != null || writings.Count == 0) return;
            try
            {
                var camera = Utils.GetMainCamera();
                if (!camera) return;
                var eye = camera.transform.position;
                for (int index = writings.Count - 1; index >= 0; index--)
                {
                    var writing = writings[index];
                    var canvas = writing.Canvas;
                    if (!canvas || !writing.Text)
                    {
                        writings[index] = writings[writings.Count - 1];
                        writings.RemoveAt(writings.Count - 1);
                        continue;
                    }
                    if ((canvas.transform.position - eye).sqrMagnitude > Reach * Reach)
                    {
                        if (!writing.Hidden && canvas.enabled)
                        {
                            canvas.enabled = false;
                            writing.Hidden = true;
                        }
                    }
                    else if (writing.Hidden) Show(writing);
                }
            }
            catch (Exception error)
            {
                Failure = error.Message;
                ShowAll();
                Plugin.Fallback("Distant sign writing: " + error.Message);
            }
        }

        // The text skipped its layout while its canvas was off, so it lays out what it holds now. Left to the canvas update,
        // a canvas switched back on drew its writing only from the next frame.
        private static void Show(Writing writing)
        {
            writing.Hidden = false;
            if (!writing.Canvas) return;
            writing.Canvas.enabled = true;
            if (writing.Text) writing.Text.ForceMeshUpdate();
        }

        private static void ShowAll()
        {
            foreach (var writing in writings) if (writing.Hidden) Show(writing);
        }
    }
}
