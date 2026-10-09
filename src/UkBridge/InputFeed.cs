using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace V1Bridge.Uk
{
    // Risk of Rain 2's keys and mouse, played into ULTRAKILL through a keyboard and mouse of our own in its Input
    // System, so every binding, hold and double tap is ULTRAKILL's (Ultracraft's method, MIT).
    public partial class Bridge
    {
        Keyboard kb;
        Mouse mouse;
        readonly HashSet<Key> held = new HashSet<Key>();
        Vector2 mouseDelta;
        float wheel;
        int buttons;
        // the newest input a frame drawn now would show, and when Risk of Rain 2 sent it
        int appliedInputSeq, pendingInputSeq;
        long appliedInputAt, pendingInputAt;
        static readonly AccessTools.FieldRef<CameraController, bool> CamZooming = AccessTools.FieldRefAccess<CameraController, bool>("zooming");

        /// <summary>IN seq sentAt dx dy wheel buttons KeyCode...</summary>
        void OnInput(string rest)
        {
            var a = rest.Split(' ');
            pendingInputSeq = int.Parse(a[0]);
            pendingInputAt = long.Parse(a[1]);
            mouseDelta += new Vector2(F(a[2]), F(a[3]));
            wheel += F(a[4]);
            buttons = int.Parse(a[5]);
            held.Clear();
            for (int i = 6; i < a.Length; i++)
            {
                if (a[i].Length == 0) continue;
                var k = ToKey((KeyCode)int.Parse(a[i]));
                if (k != Key.None) held.Add(k);
            }
        }

        void ReleaseInput()
        {
            held.Clear();
            buttons = 0;
            mouseDelta = Vector2.zero;
            wheel = 0;
            if (kb != null) InputSystem.QueueStateEvent(kb, new KeyboardState());
            if (mouse != null) InputSystem.QueueStateEvent(mouse, new MouseState());
        }

        void InjectInput()
        {
            if (!Net.Connected) return;
            if (kb == null)
            {
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                kb = InputSystem.AddDevice<Keyboard>("V1BridgeKeyboard");
                mouse = InputSystem.AddDevice<Mouse>("V1BridgeMouse");
            }
            var ks = new KeyboardState();
            foreach (var k in held) ks.Set(k, true);
            InputSystem.QueueStateEvent(kb, ks);

            var cc = levelPrepared ? MonoSingleton<CameraController>.Instance : null;
            var opm = levelPrepared ? MonoSingleton<OptionsManager>.Instance : null;
            var nm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
            if (cc != null && nm != null && v1Landed && !nm.dead && !cc.enabled) cc.enabled = true;
            var ww = levelPrepared ? MonoSingleton<WeaponWheel>.Instance : null;
            bool wheelOpen = ww != null && ww.gameObject.activeInHierarchy;
            if (cc != null && opm != null && cc.activated && mouseDelta != Vector2.zero && !wheelOpen)
            {
                // ULTRAKILL's own look maths: Look = <Mouse>/delta * 0.05, then * sensitivity / 10, slower while zoomed
                float sens = opm.mouseSensitivity / 10f * 0.05f;
                float zoom = CamZooming(cc) && cc.defaultFov > 0f ? cc.cam.fieldOfView / cc.defaultFov : 1f;
                cc.rotationX += (cc.reverseY ? -mouseDelta.y : mouseDelta.y) * sens * zoom;
                cc.rotationY += (cc.reverseX ? -mouseDelta.x : mouseDelta.x) * sens * zoom;
                float f = Mathf.DeltaAngle(0f, cc.rotationX);
                if (Mathf.Abs(f) > 90f) cc.rotationX = 90f * Mathf.Sign(f);
                if (!cc.isActiveAndEnabled || (GameStateManager.Instance != null && GameStateManager.Instance.CameraLocked)) cc.ApplyRotations();
            }
            var ms = new MouseState { delta = wheelOpen ? mouseDelta : Vector2.zero, scroll = new Vector2(0, wheel), buttons = (ushort)buttons };
            InputSystem.QueueStateEvent(mouse, ms);
            mouseDelta = Vector2.zero;
            wheel = 0;
            appliedInputSeq = pendingInputSeq;
            appliedInputAt = pendingInputAt;
        }

        /// <summary>Unity's KeyCode (what Risk of Rain 2 reports) to the Input System's Key.</summary>
        static Key ToKey(KeyCode c)
        {
            if (c >= KeyCode.A && c <= KeyCode.Z) return Key.A + (c - KeyCode.A);
            if (c >= KeyCode.Alpha1 && c <= KeyCode.Alpha9) return Key.Digit1 + (c - KeyCode.Alpha1);
            if (c >= KeyCode.F1 && c <= KeyCode.F12) return Key.F1 + (c - KeyCode.F1);
            switch (c)
            {
                case KeyCode.Alpha0: return Key.Digit0;
                case KeyCode.Space: return Key.Space;
                case KeyCode.LeftShift: return Key.LeftShift;
                case KeyCode.RightShift: return Key.RightShift;
                case KeyCode.LeftControl: return Key.LeftCtrl;
                case KeyCode.RightControl: return Key.RightCtrl;
                case KeyCode.LeftAlt: return Key.LeftAlt;
                case KeyCode.RightAlt: return Key.RightAlt;
                case KeyCode.Tab: return Key.Tab;
                case KeyCode.Return: return Key.Enter;
                case KeyCode.Backspace: return Key.Backspace;
                case KeyCode.BackQuote: return Key.Backquote;
                case KeyCode.CapsLock: return Key.CapsLock;
                case KeyCode.UpArrow: return Key.UpArrow;
                case KeyCode.DownArrow: return Key.DownArrow;
                case KeyCode.LeftArrow: return Key.LeftArrow;
                case KeyCode.RightArrow: return Key.RightArrow;
                case KeyCode.Comma: return Key.Comma;
                case KeyCode.Period: return Key.Period;
                case KeyCode.Slash: return Key.Slash;
                case KeyCode.Semicolon: return Key.Semicolon;
                case KeyCode.Quote: return Key.Quote;
                case KeyCode.Minus: return Key.Minus;
                case KeyCode.Equals: return Key.Equals;
                case KeyCode.LeftBracket: return Key.LeftBracket;
                case KeyCode.RightBracket: return Key.RightBracket;
                case KeyCode.Backslash: return Key.Backslash;
                default: return Key.None;
            }
        }
    }
}
