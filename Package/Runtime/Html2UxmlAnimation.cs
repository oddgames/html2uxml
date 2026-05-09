using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    internal sealed class Html2UxmlAnimationManipulator : Manipulator
    {
        static readonly CustomStyleProperty<string> AnimationName =
            new CustomStyleProperty<string>("--odd-animation-name");
        static readonly CustomStyleProperty<float> AnimationDurationMs =
            new CustomStyleProperty<float>("--odd-animation-duration-ms");
        static readonly CustomStyleProperty<float> AnimationDelayMs =
            new CustomStyleProperty<float>("--odd-animation-delay-ms");
        static readonly CustomStyleProperty<string> AnimationTiming =
            new CustomStyleProperty<string>("--odd-animation-timing");
        static readonly CustomStyleProperty<string> AnimationIterations =
            new CustomStyleProperty<string>("--odd-animation-iteration-count");
        static readonly CustomStyleProperty<string> AnimationDirection =
            new CustomStyleProperty<string>("--odd-animation-direction");
        static readonly CustomStyleProperty<string> AnimationFillMode =
            new CustomStyleProperty<string>("--odd-animation-fill-mode");
        static readonly CustomStyleProperty<string> AnimationPlayState =
            new CustomStyleProperty<string>("--odd-animation-play-state");
        static readonly CustomStyleProperty<string> AnimationKeyframes =
            new CustomStyleProperty<string>("--odd-animation-keyframes");

        AnimationClip _clip;
        string _timing = "linear";
        string _direction = "normal";
        string _fillMode = "none";
        bool _infinite;
        bool _paused;
        float _duration = 1f;
        float _delay;
        float _iterations = 1f;
        float _startTime;
        bool _registered;

        public VisualElement Element => target;
        public bool IsAttached => target != null && target.panel != null;

        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<CustomStyleResolvedEvent>(OnStylesResolved);
            target.RegisterCallback<AttachToPanelEvent>(OnAttach);
            target.RegisterCallback<DetachFromPanelEvent>(OnDetach);
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<CustomStyleResolvedEvent>(OnStylesResolved);
            target.UnregisterCallback<AttachToPanelEvent>(OnAttach);
            target.UnregisterCallback<DetachFromPanelEvent>(OnDetach);
            Unregister();
        }

        void OnAttach(AttachToPanelEvent evt)
        {
            if (_clip != null && !_paused)
                Register();
        }

        void OnDetach(DetachFromPanelEvent evt)
        {
            Unregister();
        }

        void OnStylesResolved(CustomStyleResolvedEvent evt)
        {
            var style = evt.customStyle;
            if (!style.TryGetValue(AnimationKeyframes, out var keyframes) ||
                !style.TryGetValue(AnimationName, out var name))
            {
                Disable();
                return;
            }

            _clip = AnimationClip.Parse(Unquote(keyframes));
            if (_clip == null || _clip.Frames.Count == 0)
            {
                Disable();
                return;
            }

            _duration = style.TryGetValue(AnimationDurationMs, out var durationMs)
                ? Mathf.Max(0.001f, durationMs / 1000f)
                : 1f;
            _delay = style.TryGetValue(AnimationDelayMs, out var delayMs)
                ? Mathf.Max(0f, delayMs / 1000f)
                : 0f;

            _timing = style.TryGetValue(AnimationTiming, out var timing)
                ? Unquote(timing).Trim().ToLowerInvariant()
                : "linear";
            _direction = style.TryGetValue(AnimationDirection, out var direction)
                ? Unquote(direction).Trim().ToLowerInvariant()
                : "normal";
            _fillMode = style.TryGetValue(AnimationFillMode, out var fillMode)
                ? Unquote(fillMode).Trim().ToLowerInvariant()
                : "none";

            var iterationText = style.TryGetValue(AnimationIterations, out var iterations)
                ? Unquote(iterations).Trim().ToLowerInvariant()
                : "1";
            _infinite = iterationText == "infinite";
            if (!_infinite &&
                !float.TryParse(iterationText, NumberStyles.Float, CultureInfo.InvariantCulture, out _iterations))
                _iterations = 1f;
            _iterations = Mathf.Max(0f, _iterations);

            var playState = style.TryGetValue(AnimationPlayState, out var rawPlayState)
                ? Unquote(rawPlayState).Trim().ToLowerInvariant()
                : "running";
            _paused = playState == "paused" || string.Equals(Unquote(name), "none", StringComparison.OrdinalIgnoreCase);

            _startTime = Time.realtimeSinceStartup;
            if (_paused)
                Unregister();
            else
                Register();
        }

        void Disable()
        {
            _clip = null;
            _paused = true;
            Unregister();
        }

        void Register()
        {
            if (_registered)
                return;
            _registered = true;
            AnimationTicker.Register(this);
        }

        void Unregister()
        {
            if (!_registered)
                return;
            _registered = false;
            AnimationTicker.Unregister(this);
        }

        public void Tick(float now)
        {
            if (_clip == null || _paused)
                return;

            float elapsed = now - _startTime - _delay;
            if (elapsed < 0f)
            {
                if (_fillMode == "backwards" || _fillMode == "both")
                    ApplyAt(0f);
                return;
            }

            float totalCycles = elapsed / _duration;
            if (!_infinite && totalCycles >= _iterations)
            {
                if (_fillMode == "forwards" || _fillMode == "both")
                    ApplyAt(IsReverseCycle(Mathf.FloorToInt(Mathf.Max(0f, _iterations - 1f))) ? 0f : 1f);
                Unregister();
                return;
            }

            int cycle = Mathf.FloorToInt(totalCycles);
            float phase = totalCycles - cycle;
            if (IsReverseCycle(cycle))
                phase = 1f - phase;
            ApplyAt(Ease(phase, _timing));
        }

            bool IsReverseCycle(int cycle)
            {
                switch (_direction)
                {
                    case "reverse":
                        return true;
                    case "alternate":
                        return cycle % 2 == 1;
                    case "alternate-reverse":
                        return cycle % 2 == 0;
                    default:
                        return false;
                }
            }

            void ApplyAt(float offset)
            {
                _clip.Sample(offset, out var a, out var b, out var localT);
                if (a == null)
                    return;
                if (b == null)
                    b = a;

                foreach (var prop in a.Properties)
                    ApplyProperty(prop.Key, prop.Value, b.Properties.TryGetValue(prop.Key, out var end) ? end : prop.Value, localT);
                foreach (var prop in b.Properties)
                {
                    if (!a.Properties.ContainsKey(prop.Key))
                        ApplyProperty(prop.Key, prop.Value, prop.Value, localT);
                }
            }

            void ApplyProperty(string prop, string from, string to, float t)
            {
                switch (prop)
                {
                    case "opacity":
                        if (TryFloat(from, out var fo) && TryFloat(to, out var toOpacity))
                            target.style.opacity = Mathf.LerpUnclamped(fo, toOpacity, t);
                        break;
                    case "translate":
                        if (TryParseTranslate(from, out var fx, out var fy) &&
                            TryParseTranslate(to, out var tx, out var ty) &&
                            fx.Percent == tx.Percent &&
                            fy.Percent == ty.Percent)
                        {
                            target.style.translate = new Translate(
                                ToLength(UnitValue.Lerp(fx, tx, t)),
                                ToLength(UnitValue.Lerp(fy, ty, t)),
                                0f);
                        }
                        break;
                    case "rotate":
                        if (TryParseDegrees(from, out var fr) && TryParseDegrees(to, out var tr))
                            target.style.rotate = new Rotate(new Angle(Mathf.LerpUnclamped(fr, tr, t), AngleUnit.Degree));
                        break;
                    case "scale":
                        if (TryParseScale(from, out var fsx, out var fsy) &&
                            TryParseScale(to, out var tsx, out var tsy))
                        {
                            target.style.scale = new Scale(new Vector3(
                                Mathf.LerpUnclamped(fsx, tsx, t),
                                Mathf.LerpUnclamped(fsy, tsy, t),
                                1f));
                        }
                        break;
                    case "color":
                        if (ColorParser.TryParse(from, out var fc) && ColorParser.TryParse(to, out var tc))
                            target.style.color = Color.LerpUnclamped(fc, tc, t);
                        break;
                    case "background-color":
                        if (ColorParser.TryParse(from, out var fbc) && ColorParser.TryParse(to, out var tbc))
                            target.style.backgroundColor = Color.LerpUnclamped(fbc, tbc, t);
                        break;
                }
            }

        sealed class AnimationClip
        {
            public readonly List<AnimationFrame> Frames = new List<AnimationFrame>();

            public static AnimationClip Parse(string encoded)
            {
                if (string.IsNullOrWhiteSpace(encoded))
                    return null;
                var clip = new AnimationClip();
                var records = encoded.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var record in records)
                {
                    var split = record.Split(new[] { '|' }, 2);
                    if (split.Length != 2 ||
                        !float.TryParse(split[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var offset))
                        continue;
                    var frame = new AnimationFrame { Offset = Mathf.Clamp01(offset) };
                    var props = split[1].Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var prop in props)
                    {
                        var kv = prop.Split(new[] { '=' }, 2);
                        if (kv.Length != 2)
                            continue;
                        frame.Properties[kv[0]] = Uri.UnescapeDataString(kv[1]);
                    }
                    if (frame.Properties.Count > 0)
                        clip.Frames.Add(frame);
                }
                clip.Frames.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                return clip.Frames.Count > 0 ? clip : null;
            }

            public void Sample(float offset, out AnimationFrame a, out AnimationFrame b, out float t)
            {
                if (Frames.Count == 0)
                {
                    a = null;
                    b = null;
                    t = 0f;
                    return;
                }
                if (offset <= Frames[0].Offset)
                {
                    a = Frames[0];
                    b = Frames[0];
                    t = 0f;
                    return;
                }
                int last = Frames.Count - 1;
                if (offset >= Frames[last].Offset)
                {
                    a = Frames[last];
                    b = Frames[last];
                    t = 0f;
                    return;
                }
                for (int i = 0; i < last; i++)
                {
                    if (offset > Frames[i + 1].Offset)
                        continue;
                    a = Frames[i];
                    b = Frames[i + 1];
                    float span = Mathf.Max(0.0001f, b.Offset - a.Offset);
                    t = Mathf.Clamp01((offset - a.Offset) / span);
                    return;
                }
                a = Frames[last];
                b = Frames[last];
                t = 0f;
            }
        }

        sealed class AnimationFrame
        {
            public float Offset;
            public readonly Dictionary<string, string> Properties = new Dictionary<string, string>();
        }

        struct UnitValue
        {
            public float Value;
            public bool Percent;

            public static UnitValue Lerp(UnitValue a, UnitValue b, float t)
            {
                return new UnitValue
                {
                    Value = Mathf.LerpUnclamped(a.Value, b.Value, t),
                    Percent = a.Percent
                };
            }
        }

        static class AnimationTicker
        {
            static readonly List<Html2UxmlAnimationManipulator> Active = new List<Html2UxmlAnimationManipulator>();
            static IVisualElementScheduledItem _scheduled;
            static VisualElement _owner;

            public static void Register(Html2UxmlAnimationManipulator binding)
            {
                if (!Active.Contains(binding))
                    Active.Add(binding);
                EnsureScheduled(binding.Element);
            }

            public static void Unregister(Html2UxmlAnimationManipulator binding)
            {
                Active.Remove(binding);
                if (Active.Count == 0)
                {
                    Stop();
                    return;
                }
                if (_owner == binding.Element || _owner == null || _owner.panel == null)
                    Rehome();
            }

            static void EnsureScheduled(VisualElement owner)
            {
                if (owner == null || owner.panel == null)
                    return;
                if (_scheduled != null && _owner != null && _owner.panel != null)
                    return;
                _owner = owner;
                _scheduled = owner.schedule.Execute(Tick).Every(16);
            }

            static void Rehome()
            {
                Stop();
                for (int i = 0; i < Active.Count; i++)
                {
                    if (Active[i].IsAttached)
                    {
                        EnsureScheduled(Active[i].Element);
                        return;
                    }
                }
            }

            static void Stop()
            {
                if (_scheduled != null)
                    _scheduled.Pause();
                _scheduled = null;
                _owner = null;
            }

            static void Tick()
            {
                if (Active.Count == 0)
                {
                    Stop();
                    return;
                }
                if (_owner == null || _owner.panel == null)
                {
                    Rehome();
                    return;
                }
                float now = Time.realtimeSinceStartup;
                for (int i = Active.Count - 1; i >= 0; i--)
                {
                    var binding = Active[i];
                    if (!binding.IsAttached)
                    {
                        Active.RemoveAt(i);
                        continue;
                    }
                    binding.Tick(now);
                }
            }
        }

        static string Unquote(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            value = value.Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[value.Length - 1] == '"') ||
                 (value[0] == '\'' && value[value.Length - 1] == '\'')))
                return value.Substring(1, value.Length - 2);
            return value;
        }

        static float Ease(float t, string timing)
        {
            t = Mathf.Clamp01(t);
            switch (timing)
            {
                case "linear":
                    return t;
                case "ease-in":
                case "ease-in-quad":
                    return t * t;
                case "ease-out":
                case "ease-out-quad":
                    return 1f - (1f - t) * (1f - t);
                case "ease-in-cubic":
                    return t * t * t;
                case "ease-out-cubic":
                    return 1f - Mathf.Pow(1f - t, 3f);
                case "ease-in-out-cubic":
                    return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
                case "ease-in-sine":
                    return 1f - Mathf.Cos((t * Mathf.PI) / 2f);
                case "ease-out-sine":
                    return Mathf.Sin((t * Mathf.PI) / 2f);
                case "ease":
                case "ease-in-out":
                case "ease-in-out-sine":
                    return t * t * (3f - 2f * t);
                default:
                    return t * t * (3f - 2f * t);
            }
        }

        static bool TryFloat(string value, out float f)
        {
            return float.TryParse(
                (value ?? "").Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out f);
        }

        static bool TryParseTranslate(string value, out UnitValue x, out UnitValue y)
        {
            var parts = SplitWhitespace(value);
            if (parts.Count == 0)
            {
                x = default;
                y = default;
                return false;
            }
            if (!TryParseLength(parts[0], out x))
            {
                x = default;
                y = default;
                return false;
            }
            if (parts.Count > 1)
                return TryParseLength(parts[1], out y);
            y = new UnitValue { Value = 0f, Percent = false };
            return true;
        }

        static bool TryParseScale(string value, out float x, out float y)
        {
            float parsedX = 1f;
            float parsedY = 1f;
            bool ok = false;
            var parts = SplitWhitespace(value);
            if (parts.Count > 0 && TryFloat(parts[0], out parsedX))
            {
                if (parts.Count > 1 && TryFloat(parts[1], out var second))
                    parsedY = second;
                else
                    parsedY = parsedX;
                ok = true;
            }
            x = parsedX;
            y = parsedY;
            return ok;
        }

        static bool TryParseDegrees(string value, out float degrees)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            if (value.EndsWith("deg"))
                return float.TryParse(value.Substring(0, value.Length - 3), NumberStyles.Float, CultureInfo.InvariantCulture, out degrees);
            if (value.EndsWith("turn") &&
                float.TryParse(value.Substring(0, value.Length - 4), NumberStyles.Float, CultureInfo.InvariantCulture, out degrees))
            {
                degrees *= 360f;
                return true;
            }
            if (value.EndsWith("rad") &&
                float.TryParse(value.Substring(0, value.Length - 3), NumberStyles.Float, CultureInfo.InvariantCulture, out degrees))
            {
                degrees *= Mathf.Rad2Deg;
                return true;
            }
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out degrees);
        }

        static bool TryParseLength(string value, out UnitValue result)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            result = default;
            bool percent = value.EndsWith("%");
            if (percent)
                value = value.Substring(0, value.Length - 1);
            else if (value.EndsWith("px"))
                value = value.Substring(0, value.Length - 2);
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return false;
            result = new UnitValue { Value = parsed, Percent = percent };
            return true;
        }

        static Length ToLength(UnitValue value)
        {
            return new Length(value.Value, value.Percent ? LengthUnit.Percent : LengthUnit.Pixel);
        }

        static List<string> SplitWhitespace(string value)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
                return result;
            var parts = value.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            result.AddRange(parts);
            return result;
        }
    }
}
