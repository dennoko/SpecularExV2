#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using lilToon;

namespace Dennokoworks.SpecularExV2
{
    public class SpecularExV2Inspector : lilToonInspector
    {
        // -- Specular 2nd --
        MaterialProperty _CustomRefl2ndUIEnabled;
        MaterialProperty _CustomRefl2ndEnabled;
        MaterialProperty _CustomRefl2ndColor;
        MaterialProperty _CustomRefl2ndStrength;
        MaterialProperty _CustomRefl2ndMode;
        MaterialProperty _CustomRefl2ndSmoothness;
        MaterialProperty _CustomRefl2ndMetallic;
        MaterialProperty _CustomRefl2ndReflectance;
        MaterialProperty _CustomRefl2ndNormalStrength;
        MaterialProperty _CustomRefl2ndShadowAttenuation;
        MaterialProperty _CustomRefl2ndMainColorStrength;
        MaterialProperty _CustomRefl2ndApplyFA;
        MaterialProperty _CustomRefl2ndFakeLightBlend;
        MaterialProperty _CustomRefl2ndFakeLightDir;
        MaterialProperty _CustomRefl2ndEnableLighting;
        MaterialProperty _CustomRefl2ndClearCoat;
        MaterialProperty _CustomRefl2ndFresnelStrength;
        MaterialProperty _CustomRefl2ndFresnelPower;
        MaterialProperty _CustomRefl2ndMaskTex;
        MaterialProperty _CustomRefl2ndNoiseStrength;
        MaterialProperty _CustomRefl2ndNoiseST;

        // -- Specular 3rd --
        MaterialProperty _CustomRefl3rdUIEnabled;
        MaterialProperty _CustomRefl3rdEnabled;
        MaterialProperty _CustomRefl3rdColor;
        MaterialProperty _CustomRefl3rdStrength;
        MaterialProperty _CustomRefl3rdMode;
        MaterialProperty _CustomRefl3rdSmoothness;
        MaterialProperty _CustomRefl3rdMetallic;
        MaterialProperty _CustomRefl3rdReflectance;
        MaterialProperty _CustomRefl3rdNormalStrength;
        MaterialProperty _CustomRefl3rdShadowAttenuation;
        MaterialProperty _CustomRefl3rdMainColorStrength;
        MaterialProperty _CustomRefl3rdApplyFA;
        MaterialProperty _CustomRefl3rdFakeLightBlend;
        MaterialProperty _CustomRefl3rdFakeLightDir;
        MaterialProperty _CustomRefl3rdEnableLighting;
        MaterialProperty _CustomRefl3rdClearCoat;
        MaterialProperty _CustomRefl3rdFresnelStrength;
        MaterialProperty _CustomRefl3rdFresnelPower;
        MaterialProperty _CustomRefl3rdMaskTex;
        MaterialProperty _CustomRefl3rdNoiseStrength;
        MaterialProperty _CustomRefl3rdNoiseST;

        // -- World MatCap --
        MaterialProperty _CustomMatcapUIEnabled;
        MaterialProperty _CustomMatcapEnabled;
        MaterialProperty _CustomMatcapFrontTex;
        MaterialProperty _CustomMatcapColor;
        MaterialProperty _CustomMatcapAlpha;
        MaterialProperty _CustomMatcapBlendMode;
        MaterialProperty _CustomMatcapBlur;
        MaterialProperty _CustomMatcapWorldFixed;
        MaterialProperty _CustomMatcapWorldRotation;
        MaterialProperty _CustomMatcapNormalStrength;
        MaterialProperty _CustomMatcapEnableLighting;
        MaterialProperty _CustomMatcapShadowStrength;
        MaterialProperty _CustomMatcapDisableBackface;
        MaterialProperty _CustomMatcapApplyFA;
        MaterialProperty _CustomMatcapHSVG;
        MaterialProperty _CustomMatcapMainColorStrength;
        MaterialProperty _CustomMatcapMaskTex;
        MaterialProperty _CustomMatcapNoiseStrength;
        MaterialProperty _CustomMatcapNoiseST;

        // -- Normal Map 3rd / 4th --
        MaterialProperty _CustomNormal3rdUIEnabled;
        MaterialProperty _CustomNormal3rdEnabled;
        MaterialProperty _CustomNormal3rdTex;
        MaterialProperty _CustomNormal3rdStrength;
        MaterialProperty _CustomNormal3rdTex_UVMode;
        MaterialProperty _CustomNormal3rdTex_ScrollRotate;
        MaterialProperty _CustomNormal3rdDistanceFade;
        MaterialProperty _CustomNormal3rdMaskTex;
        MaterialProperty _CustomNormal4thUIEnabled;
        MaterialProperty _CustomNormal4thEnabled;
        MaterialProperty _CustomNormal4thTex;
        MaterialProperty _CustomNormal4thStrength;
        MaterialProperty _CustomNormal4thTex_UVMode;
        MaterialProperty _CustomNormal4thTex_ScrollRotate;
        MaterialProperty _CustomNormal4thDistanceFade;
        MaterialProperty _CustomNormal4thMaskTex;

        // -- Rim Light 2nd --
        MaterialProperty _CustomRim2ndUIEnabled;
        MaterialProperty _CustomRim2ndEnabled;
        MaterialProperty _CustomRim2ndColor;
        MaterialProperty _CustomRim2ndStrength;
        MaterialProperty _CustomRim2ndPower;
        MaterialProperty _CustomRim2ndBlur;
        MaterialProperty _CustomRim2ndBorder;
        MaterialProperty _CustomRim2ndVerticalBias;
        MaterialProperty _CustomRim2ndBacklight;
        MaterialProperty _CustomRim2ndEnableLighting;
        MaterialProperty _CustomRim2ndBlendMode;
        MaterialProperty _CustomRim2ndNormalStrength;
        MaterialProperty _CustomRim2ndShadowAttenuation;
        MaterialProperty _CustomRim2ndMainColorStrength;
        MaterialProperty _CustomRim2ndMaskTex;
        MaterialProperty _CustomRim2ndNoiseStrength;
        MaterialProperty _CustomRim2ndNoiseST;

        // -- Rim Light 3rd --
        MaterialProperty _CustomRim3rdUIEnabled;
        MaterialProperty _CustomRim3rdEnabled;
        MaterialProperty _CustomRim3rdColor;
        MaterialProperty _CustomRim3rdStrength;
        MaterialProperty _CustomRim3rdPower;
        MaterialProperty _CustomRim3rdBlur;
        MaterialProperty _CustomRim3rdBorder;
        MaterialProperty _CustomRim3rdVerticalBias;
        MaterialProperty _CustomRim3rdBacklight;
        MaterialProperty _CustomRim3rdEnableLighting;
        MaterialProperty _CustomRim3rdBlendMode;
        MaterialProperty _CustomRim3rdNormalStrength;
        MaterialProperty _CustomRim3rdShadowAttenuation;
        MaterialProperty _CustomRim3rdMainColorStrength;
        MaterialProperty _CustomRim3rdMaskTex;
        MaterialProperty _CustomRim3rdNoiseStrength;
        MaterialProperty _CustomRim3rdNoiseST;

        // -- Shared noise mask --
        MaterialProperty _CustomNoiseMaskTex;

        // Foldout states
        static bool _foldRefl2nd;
        static bool _foldRefl3rd;
        static bool _foldMatcap;
        static bool _foldNormal3rd;
        static bool _foldNormal4th;
        static bool _foldRim2nd;
        static bool _foldRim3rd;
        static bool _foldNoise;
        static bool _foldPacking;
        static readonly List<Material> _packingMats = new List<Material>();
        static GUIStyle _packingLineStyle;

        // Copy/paste buffer
        static readonly Dictionary<string, float>   _clipFloats   = new Dictionary<string, float>();
        static readonly Dictionary<string, Color>   _clipColors   = new Dictionary<string, Color>();
        static readonly Dictionary<string, Vector4> _clipVectors  = new Dictionary<string, Vector4>();
        static readonly Dictionary<string, Texture> _clipTextures = new Dictionary<string, Texture>();
        static readonly Dictionary<string, Vector4> _clipST       = new Dictionary<string, Vector4>();
        static bool _clipHasContent;

        private const string shaderName = SpecularExMaskPacker.ShaderNameRoot;

        static string Loc(string key) => SpecularExLanguage.Get(key);

        protected override void LoadCustomProperties(MaterialProperty[] props, Material material)
        {
            isCustomShader = true;
            ReplaceToCustomShaders();
            isShowRenderMode = !material.shader.name.Contains("Optional");

            _CustomRefl2ndUIEnabled          = FindProperty("_CustomRefl2ndUIEnabled",          props, false);
            _CustomRefl2ndEnabled            = FindProperty("_CustomRefl2ndEnabled",            props, false);
            _CustomRefl2ndColor              = FindProperty("_CustomRefl2ndColor",              props, false);
            _CustomRefl2ndStrength           = FindProperty("_CustomRefl2ndStrength",           props, false);
            _CustomRefl2ndMode               = FindProperty("_CustomRefl2ndMode",               props, false);
            _CustomRefl2ndSmoothness         = FindProperty("_CustomRefl2ndSmoothness",         props, false);
            _CustomRefl2ndMetallic           = FindProperty("_CustomRefl2ndMetallic",           props, false);
            _CustomRefl2ndReflectance        = FindProperty("_CustomRefl2ndReflectance",        props, false);
            _CustomRefl2ndNormalStrength     = FindProperty("_CustomRefl2ndNormalStrength",     props, false);
            _CustomRefl2ndShadowAttenuation  = FindProperty("_CustomRefl2ndShadowAttenuation",  props, false);
            _CustomRefl2ndMainColorStrength  = FindProperty("_CustomRefl2ndMainColorStrength",  props, false);
            _CustomRefl2ndApplyFA            = FindProperty("_CustomRefl2ndApplyFA",            props, false);
            _CustomRefl2ndFakeLightBlend = FindProperty("_CustomRefl2ndFakeLightBlend", props, false);
            _CustomRefl2ndFakeLightDir = FindProperty("_CustomRefl2ndFakeLightDir", props, false);
            _CustomRefl2ndEnableLighting = FindProperty("_CustomRefl2ndEnableLighting", props, false);
            _CustomRefl2ndClearCoat = FindProperty("_CustomRefl2ndClearCoat", props, false);
            _CustomRefl2ndFresnelStrength = FindProperty("_CustomRefl2ndFresnelStrength", props, false);
            _CustomRefl2ndFresnelPower = FindProperty("_CustomRefl2ndFresnelPower", props, false);
            _CustomRefl2ndMaskTex            = FindProperty("_CustomRefl2ndMaskTex",            props, false);
            _CustomRefl2ndNoiseStrength = FindProperty("_CustomRefl2ndNoiseStrength", props, false);
            _CustomRefl2ndNoiseST       = FindProperty("_CustomRefl2ndNoiseST",       props, false);

            _CustomRefl3rdUIEnabled          = FindProperty("_CustomRefl3rdUIEnabled",          props, false);
            _CustomRefl3rdEnabled            = FindProperty("_CustomRefl3rdEnabled",            props, false);
            _CustomRefl3rdColor              = FindProperty("_CustomRefl3rdColor",              props, false);
            _CustomRefl3rdStrength           = FindProperty("_CustomRefl3rdStrength",           props, false);
            _CustomRefl3rdMode               = FindProperty("_CustomRefl3rdMode",               props, false);
            _CustomRefl3rdSmoothness         = FindProperty("_CustomRefl3rdSmoothness",         props, false);
            _CustomRefl3rdMetallic           = FindProperty("_CustomRefl3rdMetallic",           props, false);
            _CustomRefl3rdReflectance        = FindProperty("_CustomRefl3rdReflectance",        props, false);
            _CustomRefl3rdNormalStrength     = FindProperty("_CustomRefl3rdNormalStrength",     props, false);
            _CustomRefl3rdShadowAttenuation  = FindProperty("_CustomRefl3rdShadowAttenuation",  props, false);
            _CustomRefl3rdMainColorStrength  = FindProperty("_CustomRefl3rdMainColorStrength",  props, false);
            _CustomRefl3rdApplyFA            = FindProperty("_CustomRefl3rdApplyFA",            props, false);
            _CustomRefl3rdFakeLightBlend = FindProperty("_CustomRefl3rdFakeLightBlend", props, false);
            _CustomRefl3rdFakeLightDir = FindProperty("_CustomRefl3rdFakeLightDir", props, false);
            _CustomRefl3rdEnableLighting = FindProperty("_CustomRefl3rdEnableLighting", props, false);
            _CustomRefl3rdClearCoat = FindProperty("_CustomRefl3rdClearCoat", props, false);
            _CustomRefl3rdFresnelStrength = FindProperty("_CustomRefl3rdFresnelStrength", props, false);
            _CustomRefl3rdFresnelPower = FindProperty("_CustomRefl3rdFresnelPower", props, false);
            _CustomRefl3rdMaskTex            = FindProperty("_CustomRefl3rdMaskTex",            props, false);
            _CustomRefl3rdNoiseStrength = FindProperty("_CustomRefl3rdNoiseStrength", props, false);
            _CustomRefl3rdNoiseST       = FindProperty("_CustomRefl3rdNoiseST",       props, false);

            _CustomMatcapUIEnabled           = FindProperty("_CustomMatcapUIEnabled",           props, false);
            _CustomMatcapEnabled             = FindProperty("_CustomMatcapEnabled",             props, false);
            _CustomMatcapFrontTex            = FindProperty("_CustomMatcapFrontTex",            props, false);
            _CustomMatcapColor               = FindProperty("_CustomMatcapColor",               props, false);
            _CustomMatcapAlpha               = FindProperty("_CustomMatcapAlpha",               props, false);
            _CustomMatcapBlendMode           = FindProperty("_CustomMatcapBlendMode",           props, false);
            _CustomMatcapBlur                = FindProperty("_CustomMatcapBlur",                props, false);
            _CustomMatcapWorldFixed          = FindProperty("_CustomMatcapWorldFixed",          props, false);
            _CustomMatcapWorldRotation       = FindProperty("_CustomMatcapWorldRotation",       props, false);
            _CustomMatcapNormalStrength      = FindProperty("_CustomMatcapNormalStrength",      props, false);
            _CustomMatcapEnableLighting      = FindProperty("_CustomMatcapEnableLighting",      props, false);
            _CustomMatcapShadowStrength      = FindProperty("_CustomMatcapShadowStrength",      props, false);
            _CustomMatcapDisableBackface     = FindProperty("_CustomMatcapDisableBackface",     props, false);
            _CustomMatcapApplyFA             = FindProperty("_CustomMatcapApplyFA",             props, false);
            _CustomMatcapHSVG = FindProperty("_CustomMatcapHSVG", props, false);
            _CustomMatcapMainColorStrength = FindProperty("_CustomMatcapMainColorStrength", props, false);
            _CustomMatcapMaskTex             = FindProperty("_CustomMatcapMaskTex",             props, false);
            _CustomMatcapNoiseStrength = FindProperty("_CustomMatcapNoiseStrength", props, false);
            _CustomMatcapNoiseST       = FindProperty("_CustomMatcapNoiseST",       props, false);

            _CustomNormal3rdUIEnabled        = FindProperty("_CustomNormal3rdUIEnabled",        props, false);
            _CustomNormal3rdEnabled          = FindProperty("_CustomNormal3rdEnabled",          props, false);
            _CustomNormal3rdTex              = FindProperty("_CustomNormal3rdTex",              props, false);
            _CustomNormal3rdStrength         = FindProperty("_CustomNormal3rdStrength",         props, false);
            _CustomNormal3rdTex_UVMode       = FindProperty("_CustomNormal3rdTex_UVMode",       props, false);
            _CustomNormal3rdTex_ScrollRotate = FindProperty("_CustomNormal3rdTex_ScrollRotate", props, false);
            _CustomNormal3rdDistanceFade = FindProperty("_CustomNormal3rdDistanceFade", props, false);
            _CustomNormal3rdMaskTex          = FindProperty("_CustomNormal3rdMaskTex",          props, false);
            _CustomNormal4thUIEnabled        = FindProperty("_CustomNormal4thUIEnabled",        props, false);
            _CustomNormal4thEnabled          = FindProperty("_CustomNormal4thEnabled",          props, false);
            _CustomNormal4thTex              = FindProperty("_CustomNormal4thTex",              props, false);
            _CustomNormal4thStrength         = FindProperty("_CustomNormal4thStrength",         props, false);
            _CustomNormal4thTex_UVMode       = FindProperty("_CustomNormal4thTex_UVMode",       props, false);
            _CustomNormal4thTex_ScrollRotate = FindProperty("_CustomNormal4thTex_ScrollRotate", props, false);
            _CustomNormal4thDistanceFade = FindProperty("_CustomNormal4thDistanceFade", props, false);
            _CustomNormal4thMaskTex          = FindProperty("_CustomNormal4thMaskTex",          props, false);

            _CustomRim2ndUIEnabled           = FindProperty("_CustomRim2ndUIEnabled",           props, false);
            _CustomRim2ndEnabled             = FindProperty("_CustomRim2ndEnabled",             props, false);
            _CustomRim2ndColor               = FindProperty("_CustomRim2ndColor",               props, false);
            _CustomRim2ndStrength            = FindProperty("_CustomRim2ndStrength",            props, false);
            _CustomRim2ndPower               = FindProperty("_CustomRim2ndPower",               props, false);
            _CustomRim2ndBlur                = FindProperty("_CustomRim2ndBlur",                props, false);
            _CustomRim2ndBorder = FindProperty("_CustomRim2ndBorder", props, false);
            _CustomRim2ndVerticalBias = FindProperty("_CustomRim2ndVerticalBias", props, false);
            _CustomRim2ndBacklight = FindProperty("_CustomRim2ndBacklight", props, false);
            _CustomRim2ndEnableLighting = FindProperty("_CustomRim2ndEnableLighting", props, false);
            _CustomRim2ndBlendMode           = FindProperty("_CustomRim2ndBlendMode",           props, false);
            _CustomRim2ndNormalStrength      = FindProperty("_CustomRim2ndNormalStrength",      props, false);
            _CustomRim2ndShadowAttenuation   = FindProperty("_CustomRim2ndShadowAttenuation",   props, false);
            _CustomRim2ndMainColorStrength   = FindProperty("_CustomRim2ndMainColorStrength",   props, false);
            _CustomRim2ndMaskTex             = FindProperty("_CustomRim2ndMaskTex",             props, false);
            _CustomRim2ndNoiseStrength = FindProperty("_CustomRim2ndNoiseStrength", props, false);
            _CustomRim2ndNoiseST       = FindProperty("_CustomRim2ndNoiseST",       props, false);

            _CustomRim3rdUIEnabled           = FindProperty("_CustomRim3rdUIEnabled",           props, false);
            _CustomRim3rdEnabled             = FindProperty("_CustomRim3rdEnabled",             props, false);
            _CustomRim3rdColor               = FindProperty("_CustomRim3rdColor",               props, false);
            _CustomRim3rdStrength            = FindProperty("_CustomRim3rdStrength",            props, false);
            _CustomRim3rdPower               = FindProperty("_CustomRim3rdPower",               props, false);
            _CustomRim3rdBlur                = FindProperty("_CustomRim3rdBlur",                props, false);
            _CustomRim3rdBorder              = FindProperty("_CustomRim3rdBorder",              props, false);
            _CustomRim3rdVerticalBias        = FindProperty("_CustomRim3rdVerticalBias",        props, false);
            _CustomRim3rdBacklight           = FindProperty("_CustomRim3rdBacklight",           props, false);
            _CustomRim3rdEnableLighting      = FindProperty("_CustomRim3rdEnableLighting",      props, false);
            _CustomRim3rdBlendMode           = FindProperty("_CustomRim3rdBlendMode",           props, false);
            _CustomRim3rdNormalStrength      = FindProperty("_CustomRim3rdNormalStrength",      props, false);
            _CustomRim3rdShadowAttenuation   = FindProperty("_CustomRim3rdShadowAttenuation",   props, false);
            _CustomRim3rdMainColorStrength   = FindProperty("_CustomRim3rdMainColorStrength",   props, false);
            _CustomRim3rdMaskTex             = FindProperty("_CustomRim3rdMaskTex",             props, false);
            _CustomNoiseMaskTex = FindProperty("_CustomNoiseMaskTex", props, false);
            _CustomRim3rdNoiseStrength = FindProperty("_CustomRim3rdNoiseStrength", props, false);
            _CustomRim3rdNoiseST       = FindProperty("_CustomRim3rdNoiseST",       props, false);

            // One-time legacy migration on initial material load: if an older material had Enabled=1
            // but UIEnabled=0, set UIEnabled=1 so the inspector displays it as active.
            if (material != null)
            {
                MigrateLegacyUIProperty(material, "_CustomRefl2ndEnabled",   "_CustomRefl2ndUIEnabled",   _CustomRefl2ndUIEnabled);
                MigrateLegacyUIProperty(material, "_CustomRefl3rdEnabled",   "_CustomRefl3rdUIEnabled",   _CustomRefl3rdUIEnabled);
                MigrateLegacyUIProperty(material, "_CustomMatcapEnabled",    "_CustomMatcapUIEnabled",    _CustomMatcapUIEnabled);
                MigrateLegacyUIProperty(material, "_CustomNormal3rdEnabled", "_CustomNormal3rdUIEnabled", _CustomNormal3rdUIEnabled);
                MigrateLegacyUIProperty(material, "_CustomNormal4thEnabled", "_CustomNormal4thUIEnabled", _CustomNormal4thUIEnabled);
                MigrateLegacyUIProperty(material, "_CustomRim2ndEnabled",    "_CustomRim2ndUIEnabled",    _CustomRim2ndUIEnabled);
                MigrateLegacyUIProperty(material, "_CustomRim3rdEnabled",    "_CustomRim3rdUIEnabled",    _CustomRim3rdUIEnabled);
            }
        }

        void MigrateLegacyUIProperty(Material m, string enabledProp, string uiProp, MaterialProperty uiMaterialProp)
        {
            if (!m.HasProperty(enabledProp) || !m.HasProperty(uiProp)) return;
            if (m.GetFloat(enabledProp) > 0.5f && m.GetFloat(uiProp) < 0.5f)
            {
                m.SetFloat(uiProp, 1f);
                if (uiMaterialProp != null) uiMaterialProp.floatValue = 1f;
                EditorUtility.SetDirty(m);
            }
        }

        private static GUIStyle _versionLinkStyle;

        private void DrawVersionBar()
        {
            SpecularExVersion.StartCheckBackgroundTask();
            var result = SpecularExVersion.LoadResultFromSessionState();

            if (_versionLinkStyle == null)
            {
                _versionLinkStyle = new GUIStyle(EditorStyles.miniLabel);
            }

            var prevColor = GUI.contentColor;

            EditorGUILayout.BeginHorizontal();

            GUI.contentColor = new Color(0.68f, 0.68f, 0.68f);
            GUILayout.Label($"SpecularExV2 v{result.LocalVersion}", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            GUI.contentColor = prevColor;

            switch (result.State)
            {
                case DennokoVersionChecker.State.UpdateAvailable:
                {
                    var tooltip = string.IsNullOrEmpty(result.Message)
                        ? Loc("version_update_tooltip")
                        : result.Message;

                    GUI.contentColor = new Color(0.35f, 0.8f, 0.4f);
                    var text = SpecularExLanguage.Format("version_update_available", result.LatestVersion);
                    var clicked = GUILayout.Button(
                        new GUIContent(text, tooltip),
                        _versionLinkStyle, GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;

                    EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
                    if (clicked)
                    {
                        SpecularExVersion.OpenUpdatePage(result.Url);
                    }
                    break;
                }

                case DennokoVersionChecker.State.Error:
                    GUI.contentColor = new Color(1f, 0.72f, 0.3f);
                    GUILayout.Label(
                        new GUIContent(Loc("version_error"), Loc("version_error_tooltip")),
                        EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;
                    break;

                case DennokoVersionChecker.State.Checking:
                    GUI.contentColor = new Color(0.55f, 0.55f, 0.55f);
                    GUILayout.Label(Loc("version_checking"), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;
                    break;

                default:
                    break;
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(new GUIContent("↻", Loc("version_recheck_tooltip")), EditorStyles.miniButton, GUILayout.Width(22)))
            {
                SpecularExVersion.ForceRecheck();
            }

            EditorGUILayout.EndHorizontal();
        }

        protected override void DrawCustomProperties(Material material)
        {
            // Keep the effective flags consistent before anything is drawn (covers materials edited
            // elsewhere, pasted values and Undo).
            SyncAllEffective();

            DrawVersionBar();
            DrawPackingStatusLine();

            DrawRefl2nd();
            DrawRefl3rd();
            DrawMatcap();
            DrawNormal3rd();
            DrawNormal4th();
            DrawRim2nd();
            DrawRim3rd();
            DrawNoiseMask();
            DrawPackingStatus();

            SyncAllEffective();

            // Queues a packed-mask update when the slots differ from the last check: covers opening the
            // material, editing/pasting/undoing slots, and switching a material to SpecularExV2.
            foreach (var t in m_MaterialEditor.targets)
                if (t is Material mm) SpecularExPackedMaskWatcher.RequestIfSlotsChanged(mm);
        }

        // ========================================================================
        //  Dual-Property sync
        // ========================================================================
        // _CustomXxxUIEnabled is what the user toggles; _CustomXxxEnabled is what the shader reads. The
        // effective flag is 1 only when the UI toggle is on AND the feature's required texture is set,
        // so the GPU never branches into a layer that would sample an empty texture.
        // Done per material (not via MaterialProperty) so multi-selection with mixed textures is exact.
        void SyncAllEffective()
        {
            foreach (var t in m_MaterialEditor.targets)
            {
                if (!(t is Material m)) continue;
                SyncEffectiveEnabled(m, "_CustomRefl2ndEnabled",   "_CustomRefl2ndUIEnabled",   null);
                SyncEffectiveEnabled(m, "_CustomRefl3rdEnabled",   "_CustomRefl3rdUIEnabled",   null);
                SyncEffectiveEnabled(m, "_CustomMatcapEnabled",    "_CustomMatcapUIEnabled",    "_CustomMatcapFrontTex");
                SyncEffectiveEnabled(m, "_CustomNormal3rdEnabled", "_CustomNormal3rdUIEnabled", "_CustomNormal3rdTex");
                SyncEffectiveEnabled(m, "_CustomNormal4thEnabled", "_CustomNormal4thUIEnabled", "_CustomNormal4thTex");
                SyncEffectiveEnabled(m, "_CustomRim2ndEnabled",    "_CustomRim2ndUIEnabled",    null);
                SyncEffectiveEnabled(m, "_CustomRim3rdEnabled",    "_CustomRim3rdUIEnabled",    null);
            }

            SyncPropertyEffective(_CustomRefl2ndEnabled,   _CustomRefl2ndUIEnabled,   null);
            SyncPropertyEffective(_CustomRefl3rdEnabled,   _CustomRefl3rdUIEnabled,   null);
            SyncPropertyEffective(_CustomMatcapEnabled,    _CustomMatcapUIEnabled,    _CustomMatcapFrontTex);
            SyncPropertyEffective(_CustomNormal3rdEnabled, _CustomNormal3rdUIEnabled, _CustomNormal3rdTex);
            SyncPropertyEffective(_CustomNormal4thEnabled, _CustomNormal4thUIEnabled, _CustomNormal4thTex);
            SyncPropertyEffective(_CustomRim2ndEnabled,    _CustomRim2ndUIEnabled,    null);
            SyncPropertyEffective(_CustomRim3rdEnabled,    _CustomRim3rdUIEnabled,    null);
        }

        static void SyncPropertyEffective(MaterialProperty enabledProp, MaterialProperty uiProp, MaterialProperty texProp)
        {
            if (enabledProp == null || uiProp == null) return;
            bool ui  = uiProp.floatValue > 0.5f;
            bool tex = texProp == null || texProp.textureValue != null;
            float target = ui && tex ? 1f : 0f;
            if (enabledProp.floatValue != target)
                enabledProp.floatValue = target;
        }

        // uiProp == null: the flag depends on the texture only. texProp == null: on the toggle only.
        static void SyncEffectiveEnabled(Material m, string enabledProp, string uiProp, string texProp)
        {
            if (!m.HasProperty(enabledProp)) return;
            bool ui  = uiProp  == null || (m.HasProperty(uiProp) && m.GetFloat(uiProp) > 0.5f);
            bool tex = texProp == null || (m.HasProperty(texProp) && m.GetTexture(texProp) != null);
            float target = ui && tex ? 1f : 0f;
            if (m.GetFloat(enabledProp) == target) return;
            m.SetFloat(enabledProp, target);
            EditorUtility.SetDirty(m);
        }

        // ========================================================================
        //  Helpers
        // ========================================================================
        void DrawToggle(MaterialProperty uiProp, MaterialProperty enabledProp, MaterialProperty texProp, string label)
        {
            if (uiProp == null) return;
            EditorGUI.showMixedValue = uiProp.hasMixedValue;
            EditorGUI.BeginChangeCheck();
            bool on = EditorGUI.ToggleLeft(EditorGUILayout.GetControlRect(), label, uiProp.floatValue > 0.5f, customToggleFont);
            if (EditorGUI.EndChangeCheck())
            {
                m_MaterialEditor.RegisterPropertyChangeUndo(label);
                float uiVal = on ? 1f : 0f;
                uiProp.floatValue = uiVal;

                if (enabledProp != null)
                {
                    bool hasTex = texProp == null || texProp.textureValue != null;
                    enabledProp.floatValue = (on && hasTex) ? 1f : 0f;
                }

                foreach (var t in m_MaterialEditor.targets)
                {
                    if (t is Material m)
                    {
                        m.SetFloat(uiProp.name, uiVal);
                        if (enabledProp != null)
                        {
                            bool mHasTex = texProp == null || (m.HasProperty(texProp.name) && m.GetTexture(texProp.name) != null);
                            m.SetFloat(enabledProp.name, (on && mHasTex) ? 1f : 0f);
                        }
                        EditorUtility.SetDirty(m);
                    }
                }
            }
            EditorGUI.showMixedValue = false;
        }

        void Prop(MaterialProperty prop, string label)
        {
            if (prop != null) m_MaterialEditor.ShaderProperty(prop, label);
        }

        void BoolProp(MaterialProperty prop, string label)
        {
            if (prop == null) return;
            EditorGUI.showMixedValue = prop.hasMixedValue;
            EditorGUI.BeginChangeCheck();
            bool on = EditorGUILayout.Toggle(label, prop.floatValue > 0.5f);
            if (EditorGUI.EndChangeCheck())
            {
                m_MaterialEditor.RegisterPropertyChangeUndo(label);
                prop.floatValue = on ? 1f : 0f;
                foreach (var t in m_MaterialEditor.targets)
                    if (t is Material m) { m.SetFloat(prop.name, prop.floatValue); EditorUtility.SetDirty(m); }
            }
            EditorGUI.showMixedValue = false;
        }

        void PopupProp(MaterialProperty prop, string label, string[] options)
        {
            if (prop == null) return;
            EditorGUI.showMixedValue = prop.hasMixedValue;
            EditorGUI.BeginChangeCheck();
            int v = EditorGUILayout.Popup(label, Mathf.Clamp((int)prop.floatValue, 0, options.Length - 1), options);
            if (EditorGUI.EndChangeCheck())
                prop.floatValue = v;
            EditorGUI.showMixedValue = false;
        }

        // One component of a Vector property as a slider (HSVG, distance fade...).
        void VecSlider(MaterialProperty prop, int index, string label, float min, float max)
        {
            if (prop == null) return;
            EditorGUI.showMixedValue = prop.hasMixedValue;
            EditorGUI.BeginChangeCheck();
            Vector4 v = prop.vectorValue;
            float f = EditorGUILayout.Slider(label, v[index], min, max);
            if (EditorGUI.EndChangeCheck()) { v[index] = f; prop.vectorValue = v; }
            EditorGUI.showMixedValue = false;
        }

        // One component of a Vector property as a float field, shown multiplied by displayScale
        // (e.g. radians stored, degrees shown).
        void VecFloat(MaterialProperty prop, int index, string label, float displayScale = 1f)
        {
            if (prop == null) return;
            EditorGUI.showMixedValue = prop.hasMixedValue;
            EditorGUI.BeginChangeCheck();
            Vector4 v = prop.vectorValue;
            float f = EditorGUILayout.FloatField(label, v[index] * displayScale);
            if (EditorGUI.EndChangeCheck()) { v[index] = f / displayScale; prop.vectorValue = v; }
            EditorGUI.showMixedValue = false;
        }

        // xy of a Vector property as one Vector2 field (UV scroll).
        void Vec2Prop(MaterialProperty prop, string label, int first = 0)
        {
            if (prop == null) return;
            EditorGUI.showMixedValue = prop.hasMixedValue;
            EditorGUI.BeginChangeCheck();
            Vector4 v = prop.vectorValue;
            Vector2 f = EditorGUILayout.Vector2Field(label, new Vector2(v[first], v[first + 1]));
            if (EditorGUI.EndChangeCheck()) { v[first] = f.x; v[first + 1] = f.y; prop.vectorValue = v; }
            EditorGUI.showMixedValue = false;
        }

        // A layer's use of the shared noise mask: strength, then (only while used) its own tiling/offset.
        void DrawNoise(MaterialProperty strength, MaterialProperty st)
        {
            if (strength == null) return;
            Prop(strength, Loc("label_noise_strength"));
            if (!strength.hasMixedValue && strength.floatValue <= 0f) return;
            Vec2Prop(st, Loc("label_noise_tiling"), 0);
            Vec2Prop(st, Loc("label_noise_offset"), 2);
            if (_CustomNoiseMaskTex != null && _CustomNoiseMaskTex.textureValue == null && !_CustomNoiseMaskTex.hasMixedValue)
                EditorGUILayout.HelpBox(Loc("help_noise_missing"), MessageType.Info);
        }

        // xyz of a Vector property as one Vector3 field (fake light direction).
        void Vec3Prop(MaterialProperty prop, string label)
        {
            if (prop == null) return;
            EditorGUI.showMixedValue = prop.hasMixedValue;
            EditorGUI.BeginChangeCheck();
            Vector4 v = prop.vectorValue;
            Vector3 f = EditorGUILayout.Vector3Field(label, new Vector3(v.x, v.y, v.z));
            if (EditorGUI.EndChangeCheck()) { v.x = f.x; v.y = f.y; v.z = f.z; prop.vectorValue = v; }
            EditorGUI.showMixedValue = false;
        }

        static bool IsOn(MaterialProperty prop) => prop != null && prop.floatValue > 0.5f;

        // Mixed selections count as on, so the fields they show are never hidden while one material uses them.
        static bool IsOnOrMixed(MaterialProperty prop) => prop != null && (prop.hasMixedValue || prop.floatValue > 0.5f);

        string[] BlendModes() => new[] { Loc("blend_normal"), Loc("blend_add"), Loc("blend_screen"), Loc("blend_mul") };

        // ========================================================================
        //  Copy/Paste section menu
        // ========================================================================
        void DrawSectionMenu(MaterialProperty[] props)
        {
            var rect = GUILayoutUtility.GetLastRect();
            rect.xMin = rect.xMax - 24f;
            rect.width = 24f;

            if (GUI.Button(rect, EditorGUIUtility.IconContent("_Popup"), new GUIStyle("IconButton")))
            {
                var captured = props;
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent(Loc("menu_copy")),          false, () => CopySection(captured, false));
                menu.AddItem(new GUIContent(Loc("menu_copy_with_tex")), false, () => CopySection(captured, true));
                menu.AddSeparator("");
                if (_clipHasContent)
                    menu.AddItem(new GUIContent(Loc("menu_paste")), false, () => PasteSection(captured));
                else
                    menu.AddDisabledItem(new GUIContent(Loc("menu_paste")));
                menu.ShowAsContext();
            }
        }

        static void CopySection(MaterialProperty[] props, bool includeTextures)
        {
            _clipFloats.Clear();
            _clipColors.Clear();
            _clipVectors.Clear();
            _clipTextures.Clear();
            _clipST.Clear();
            _clipHasContent = true;

            foreach (var p in props)
            {
                if (p == null) continue;
                switch (p.type)
                {
                    case MaterialProperty.PropType.Float:
                    case MaterialProperty.PropType.Range:
                        _clipFloats[p.name] = p.floatValue;
                        break;
                    case MaterialProperty.PropType.Color:
                        _clipColors[p.name] = p.colorValue;
                        break;
                    case MaterialProperty.PropType.Vector:
                        _clipVectors[p.name] = p.vectorValue;
                        break;
                    case MaterialProperty.PropType.Texture:
                        // Tiling/offset is part of the layer's look (mask _ST drives the packed channel).
                        _clipST[p.name] = p.textureScaleAndOffset;
                        if (includeTextures) _clipTextures[p.name] = p.textureValue;
                        break;
                }
            }
        }

        static void PasteSection(MaterialProperty[] props)
        {
            var targets = props.Where(p => p != null).SelectMany(p => p.targets).Distinct().ToArray();
            if (targets.Length > 0) Undo.RecordObjects(targets, "Paste SpecularExV2 Section");

            foreach (var p in props)
            {
                if (p == null) continue;
                switch (p.type)
                {
                    case MaterialProperty.PropType.Float:
                    case MaterialProperty.PropType.Range:
                        if (_clipFloats.TryGetValue(p.name, out float f)) p.floatValue = f;
                        break;
                    case MaterialProperty.PropType.Color:
                        if (_clipColors.TryGetValue(p.name, out Color c)) p.colorValue = c;
                        break;
                    case MaterialProperty.PropType.Vector:
                        if (_clipVectors.TryGetValue(p.name, out Vector4 v)) p.vectorValue = v;
                        break;
                    case MaterialProperty.PropType.Texture:
                        if (_clipST.TryGetValue(p.name, out Vector4 st)) p.textureScaleAndOffset = st;
                        if (_clipTextures.TryGetValue(p.name, out Texture t)) p.textureValue = t;
                        break;
                }
            }
        }

        // ========================================================================
        //  Sections
        // ========================================================================

        // -- Specular 2nd --
        void DrawRefl2nd()
        {
            _foldRefl2nd = Foldout(Loc("foldout_refl2nd"), _foldRefl2nd);
            DrawSectionMenu(new[] {
                _CustomRefl2ndUIEnabled,         _CustomRefl2ndEnabled,
                _CustomRefl2ndColor,             _CustomRefl2ndStrength,
                _CustomRefl2ndMode,              _CustomRefl2ndSmoothness,
                _CustomRefl2ndMetallic,          _CustomRefl2ndReflectance,
                _CustomRefl2ndNormalStrength,    _CustomRefl2ndShadowAttenuation,
                _CustomRefl2ndMainColorStrength, _CustomRefl2ndApplyFA,
                _CustomRefl2ndFakeLightBlend,    _CustomRefl2ndFakeLightDir,
                _CustomRefl2ndEnableLighting,
                _CustomRefl2ndClearCoat,         _CustomRefl2ndFresnelStrength,
                _CustomRefl2ndFresnelPower,      _CustomRefl2ndMaskTex, _CustomRefl2ndNoiseStrength, _CustomRefl2ndNoiseST,
            });
            if (!_foldRefl2nd) return;

            EditorGUILayout.BeginVertical(boxOuter);
            DrawToggle(_CustomRefl2ndUIEnabled, _CustomRefl2ndEnabled, null, Loc("toggle_refl2nd"));
            if (IsOn(_CustomRefl2ndUIEnabled))
            {
                EditorGUILayout.BeginVertical(boxInnerHalf);
                Prop(_CustomRefl2ndColor,    Loc("label_color"));
                Prop(_CustomRefl2ndStrength, Loc("label_strength"));
                lilEditorGUI.DrawLine();
                PopupProp(_CustomRefl2ndMode, Loc("label_mode"), new[] { Loc("mode_ggx"), Loc("mode_blinn") });
                Prop(_CustomRefl2ndSmoothness,  Loc("label_smoothness"));
                BoolProp(_CustomRefl2ndClearCoat, Loc("label_clear_coat"));
                if (!IsOnOrMixed(_CustomRefl2ndClearCoat))
                {
                    Prop(_CustomRefl2ndMetallic,    Loc("label_metallic"));
                    Prop(_CustomRefl2ndReflectance, Loc("label_reflectance"));
                }
                else EditorGUILayout.HelpBox(Loc("help_clear_coat"), MessageType.None);
                Prop(_CustomRefl2ndFresnelStrength, Loc("label_fresnel_strength"));
                Prop(_CustomRefl2ndFresnelPower,    Loc("label_fresnel_power"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRefl2ndEnableLighting, Loc("label_enable_lighting"));
                Prop(_CustomRefl2ndFakeLightBlend, Loc("label_fake_light_blend"));
                Vec3Prop(_CustomRefl2ndFakeLightDir, Loc("label_fake_light_dir"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRefl2ndNormalStrength,    Loc("label_normal_strength"));
                Prop(_CustomRefl2ndShadowAttenuation, Loc("label_shadow_attenuation"));
                Prop(_CustomRefl2ndMainColorStrength, Loc("label_main_color_strength"));
                BoolProp(_CustomRefl2ndApplyFA,       Loc("label_apply_fa"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRefl2ndMaskTex, Loc("label_mask"));
                DrawNoise(_CustomRefl2ndNoiseStrength, _CustomRefl2ndNoiseST);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndVertical();
        }

        // -- Specular 3rd --
        void DrawRefl3rd()
        {
            _foldRefl3rd = Foldout(Loc("foldout_refl3rd"), _foldRefl3rd);
            DrawSectionMenu(new[] {
                _CustomRefl3rdUIEnabled,         _CustomRefl3rdEnabled,
                _CustomRefl3rdColor,             _CustomRefl3rdStrength,
                _CustomRefl3rdMode,              _CustomRefl3rdSmoothness,
                _CustomRefl3rdMetallic,          _CustomRefl3rdReflectance,
                _CustomRefl3rdNormalStrength,    _CustomRefl3rdShadowAttenuation,
                _CustomRefl3rdMainColorStrength, _CustomRefl3rdApplyFA,
                _CustomRefl3rdFakeLightBlend,    _CustomRefl3rdFakeLightDir,
                _CustomRefl3rdEnableLighting,
                _CustomRefl3rdClearCoat,         _CustomRefl3rdFresnelStrength,
                _CustomRefl3rdFresnelPower,      _CustomRefl3rdMaskTex, _CustomRefl3rdNoiseStrength, _CustomRefl3rdNoiseST,
            });
            if (!_foldRefl3rd) return;

            EditorGUILayout.BeginVertical(boxOuter);
            DrawToggle(_CustomRefl3rdUIEnabled, _CustomRefl3rdEnabled, null, Loc("toggle_refl3rd"));
            if (IsOn(_CustomRefl3rdUIEnabled))
            {
                EditorGUILayout.BeginVertical(boxInnerHalf);
                Prop(_CustomRefl3rdColor,    Loc("label_color"));
                Prop(_CustomRefl3rdStrength, Loc("label_strength"));
                lilEditorGUI.DrawLine();
                PopupProp(_CustomRefl3rdMode, Loc("label_mode"), new[] { Loc("mode_ggx"), Loc("mode_blinn") });
                Prop(_CustomRefl3rdSmoothness,  Loc("label_smoothness"));
                BoolProp(_CustomRefl3rdClearCoat, Loc("label_clear_coat"));
                if (!IsOnOrMixed(_CustomRefl3rdClearCoat))
                {
                    Prop(_CustomRefl3rdMetallic,    Loc("label_metallic"));
                    Prop(_CustomRefl3rdReflectance, Loc("label_reflectance"));
                }
                else EditorGUILayout.HelpBox(Loc("help_clear_coat"), MessageType.None);
                Prop(_CustomRefl3rdFresnelStrength, Loc("label_fresnel_strength"));
                Prop(_CustomRefl3rdFresnelPower,    Loc("label_fresnel_power"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRefl3rdEnableLighting, Loc("label_enable_lighting"));
                Prop(_CustomRefl3rdFakeLightBlend, Loc("label_fake_light_blend"));
                Vec3Prop(_CustomRefl3rdFakeLightDir, Loc("label_fake_light_dir"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRefl3rdNormalStrength,    Loc("label_normal_strength"));
                Prop(_CustomRefl3rdShadowAttenuation, Loc("label_shadow_attenuation"));
                Prop(_CustomRefl3rdMainColorStrength, Loc("label_main_color_strength"));
                BoolProp(_CustomRefl3rdApplyFA,       Loc("label_apply_fa"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRefl3rdMaskTex, Loc("label_mask"));
                DrawNoise(_CustomRefl3rdNoiseStrength, _CustomRefl3rdNoiseST);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndVertical();
        }

        // -- World MatCap --
        void DrawMatcap()
        {
            _foldMatcap = Foldout(Loc("foldout_matcap"), _foldMatcap);
            DrawSectionMenu(new[] {
                _CustomMatcapUIEnabled,         _CustomMatcapEnabled,
                _CustomMatcapFrontTex,
                _CustomMatcapColor,             _CustomMatcapAlpha,
                _CustomMatcapBlendMode,         _CustomMatcapBlur,
                _CustomMatcapWorldFixed,        _CustomMatcapWorldRotation,  _CustomMatcapNormalStrength,
                _CustomMatcapEnableLighting,    _CustomMatcapShadowStrength,
                _CustomMatcapDisableBackface,   _CustomMatcapMaskTex, _CustomMatcapNoiseStrength, _CustomMatcapNoiseST,
                _CustomMatcapHSVG,              _CustomMatcapMainColorStrength, _CustomMatcapApplyFA,
            });
            if (!_foldMatcap) return;

            EditorGUILayout.BeginVertical(boxOuter);
            DrawToggle(_CustomMatcapUIEnabled, _CustomMatcapEnabled, _CustomMatcapFrontTex, Loc("toggle_matcap"));
            if (IsOn(_CustomMatcapUIEnabled))
            {
                EditorGUILayout.BeginVertical(boxInnerHalf);
                Prop(_CustomMatcapFrontTex, Loc("label_texture"));
                if (_CustomMatcapFrontTex != null && _CustomMatcapFrontTex.textureValue == null && !_CustomMatcapFrontTex.hasMixedValue)
                    EditorGUILayout.HelpBox(Loc("help_matcap_front_missing"), MessageType.Info);
                Prop(_CustomMatcapWorldFixed, Loc("label_world_fixing"));
                // Yaw only affects the world-fixed side. Mixed values show it too, so it is never hidden
                // while it matters.
                bool worldFixed = _CustomMatcapWorldFixed != null && (_CustomMatcapWorldFixed.hasMixedValue || _CustomMatcapWorldFixed.floatValue > 0f);
                Prop(_CustomMatcapColor, Loc("label_color"));
                Prop(_CustomMatcapAlpha, Loc("label_alpha"));
                PopupProp(_CustomMatcapBlendMode, Loc("label_blend_mode"), BlendModes());
                Prop(_CustomMatcapMainColorStrength, Loc("label_main_color_strength"));
                VecSlider(_CustomMatcapHSVG, 0, Loc("label_hue"),        -0.5f, 0.5f);
                VecSlider(_CustomMatcapHSVG, 1, Loc("label_saturation"),  0f,   2f);
                VecSlider(_CustomMatcapHSVG, 2, Loc("label_value"),       0f,   2f);
                VecSlider(_CustomMatcapHSVG, 3, Loc("label_gamma"),       0.01f, 2f);
                lilEditorGUI.DrawLine();
                Prop(_CustomMatcapBlur, Loc("label_blur"));
                if (worldFixed) Prop(_CustomMatcapWorldRotation, Loc("label_world_rotation"));
                Prop(_CustomMatcapNormalStrength, Loc("label_normal_strength"));
                lilEditorGUI.DrawLine();
                Prop(_CustomMatcapEnableLighting,     Loc("label_enable_lighting"));
                Prop(_CustomMatcapShadowStrength,     Loc("label_shadow_strength"));
                BoolProp(_CustomMatcapDisableBackface, Loc("label_disable_backface"));
                BoolProp(_CustomMatcapApplyFA,         Loc("label_matcap_apply_fa"));
                lilEditorGUI.DrawLine();
                Prop(_CustomMatcapMaskTex, Loc("label_mask"));
                DrawNoise(_CustomMatcapNoiseStrength, _CustomMatcapNoiseST);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndVertical();
        }

        // -- Normal Map 3rd / 4th --
        void DrawNormal3rd()
        {
            _foldNormal3rd = DrawNormalLayer(_foldNormal3rd, "normal3rd",
                _CustomNormal3rdUIEnabled, _CustomNormal3rdEnabled, _CustomNormal3rdTex, _CustomNormal3rdStrength,
                _CustomNormal3rdTex_UVMode, _CustomNormal3rdTex_ScrollRotate, _CustomNormal3rdDistanceFade, _CustomNormal3rdMaskTex);
        }

        void DrawNormal4th()
        {
            _foldNormal4th = DrawNormalLayer(_foldNormal4th, "normal4th",
                _CustomNormal4thUIEnabled, _CustomNormal4thEnabled, _CustomNormal4thTex, _CustomNormal4thStrength,
                _CustomNormal4thTex_UVMode, _CustomNormal4thTex_ScrollRotate, _CustomNormal4thDistanceFade, _CustomNormal4thMaskTex);
        }

        // Both layers share one layout; `key` selects the foldout/toggle labels (foldout_<key>, toggle_<key>).
        bool DrawNormalLayer(bool fold, string key,
            MaterialProperty uiEnabled, MaterialProperty enabled, MaterialProperty tex, MaterialProperty strength,
            MaterialProperty uvMode, MaterialProperty scrollRotate, MaterialProperty distanceFade, MaterialProperty mask)
        {
            fold = Foldout(Loc("foldout_" + key), fold);
            DrawSectionMenu(new[] {
                uiEnabled,     enabled,
                tex,           strength,
                uvMode,        mask,
                scrollRotate,  distanceFade,
            });
            if (!fold) return fold;

            EditorGUILayout.BeginVertical(boxOuter);
            DrawToggle(uiEnabled, enabled, tex, Loc("toggle_" + key));
            if (IsOn(uiEnabled))
            {
                EditorGUILayout.BeginVertical(boxInnerHalf);
                Prop(tex, Loc("label_normal_map"));
                if (tex != null && tex.textureValue == null && !tex.hasMixedValue)
                    EditorGUILayout.HelpBox(Loc("help_normal_missing"), MessageType.Info);
                Prop(strength, Loc("label_strength"));
                PopupProp(uvMode, Loc("label_uv_mode"), new[] { Loc("uv0"), Loc("uv1"), Loc("uv2"), Loc("uv3") });
                // Angle and rotation speed are stored in radians (lilToon ScrollRotate layout), shown in degrees.
                Vec2Prop(scrollRotate, Loc("label_uv_scroll"));
                VecFloat(scrollRotate, 2, Loc("label_uv_angle"),        Mathf.Rad2Deg);
                VecFloat(scrollRotate, 3, Loc("label_uv_rotate_speed"), Mathf.Rad2Deg);
                lilEditorGUI.DrawLine();
                VecFloat(distanceFade,  0, Loc("label_distance_fade_start"));
                VecFloat(distanceFade,  1, Loc("label_distance_fade_end"));
                VecSlider(distanceFade, 2, Loc("label_distance_fade_strength"), 0f, 1f);
                lilEditorGUI.DrawLine();
                Prop(mask, Loc("label_mask"));
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndVertical();
            return fold;
        }

        // -- Rim Light 2nd --
        void DrawRim2nd()
        {
            _foldRim2nd = Foldout(Loc("foldout_rim2nd"), _foldRim2nd);
            DrawSectionMenu(new[] {
                _CustomRim2ndUIEnabled,         _CustomRim2ndEnabled,
                _CustomRim2ndColor,             _CustomRim2ndStrength,
                _CustomRim2ndPower,             _CustomRim2ndBlur,
                _CustomRim2ndBlendMode,         _CustomRim2ndNormalStrength,
                _CustomRim2ndShadowAttenuation, _CustomRim2ndMainColorStrength,
                _CustomRim2ndMaskTex, _CustomRim2ndNoiseStrength, _CustomRim2ndNoiseST,           _CustomRim2ndBorder,
                _CustomRim2ndVerticalBias,      _CustomRim2ndBacklight,
                _CustomRim2ndEnableLighting,
            });
            if (!_foldRim2nd) return;

            EditorGUILayout.BeginVertical(boxOuter);
            DrawToggle(_CustomRim2ndUIEnabled, _CustomRim2ndEnabled, null, Loc("toggle_rim2nd"));
            if (IsOn(_CustomRim2ndUIEnabled))
            {
                EditorGUILayout.BeginVertical(boxInnerHalf);
                Prop(_CustomRim2ndColor,    Loc("label_color"));
                Prop(_CustomRim2ndStrength, Loc("label_strength"));
                PopupProp(_CustomRim2ndBlendMode, Loc("label_blend_mode"), BlendModes());
                Prop(_CustomRim2ndEnableLighting, Loc("label_enable_lighting"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRim2ndPower,  Loc("label_power"));
                Prop(_CustomRim2ndBorder, Loc("label_border"));
                Prop(_CustomRim2ndBlur,   Loc("label_rim_blur"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRim2ndVerticalBias, Loc("label_vertical_bias"));
                Prop(_CustomRim2ndBacklight,    Loc("label_backlight"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRim2ndNormalStrength,    Loc("label_normal_strength"));
                Prop(_CustomRim2ndShadowAttenuation, Loc("label_shadow_attenuation"));
                Prop(_CustomRim2ndMainColorStrength, Loc("label_main_color_strength"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRim2ndMaskTex, Loc("label_mask"));
                DrawNoise(_CustomRim2ndNoiseStrength, _CustomRim2ndNoiseST);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndVertical();
        }

        // -- Rim Light 3rd --
        void DrawRim3rd()
        {
            _foldRim3rd = Foldout(Loc("foldout_rim3rd"), _foldRim3rd);
            DrawSectionMenu(new[] {
                _CustomRim3rdUIEnabled,         _CustomRim3rdEnabled,
                _CustomRim3rdColor,             _CustomRim3rdStrength,
                _CustomRim3rdPower,             _CustomRim3rdBlur,
                _CustomRim3rdBlendMode,         _CustomRim3rdNormalStrength,
                _CustomRim3rdShadowAttenuation, _CustomRim3rdMainColorStrength,
                _CustomRim3rdMaskTex, _CustomRim3rdNoiseStrength, _CustomRim3rdNoiseST,           _CustomRim3rdBorder,
                _CustomRim3rdVerticalBias,      _CustomRim3rdBacklight,
                _CustomRim3rdEnableLighting,
            });
            if (!_foldRim3rd) return;

            EditorGUILayout.BeginVertical(boxOuter);
            DrawToggle(_CustomRim3rdUIEnabled, _CustomRim3rdEnabled, null, Loc("toggle_rim3rd"));
            if (IsOn(_CustomRim3rdUIEnabled))
            {
                EditorGUILayout.BeginVertical(boxInnerHalf);
                Prop(_CustomRim3rdColor,    Loc("label_color"));
                Prop(_CustomRim3rdStrength, Loc("label_strength"));
                PopupProp(_CustomRim3rdBlendMode, Loc("label_blend_mode"), BlendModes());
                Prop(_CustomRim3rdEnableLighting, Loc("label_enable_lighting"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRim3rdPower,  Loc("label_power"));
                Prop(_CustomRim3rdBorder, Loc("label_border"));
                Prop(_CustomRim3rdBlur,   Loc("label_rim_blur"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRim3rdVerticalBias, Loc("label_vertical_bias"));
                Prop(_CustomRim3rdBacklight,    Loc("label_backlight"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRim3rdNormalStrength,    Loc("label_normal_strength"));
                Prop(_CustomRim3rdShadowAttenuation, Loc("label_shadow_attenuation"));
                Prop(_CustomRim3rdMainColorStrength, Loc("label_main_color_strength"));
                lilEditorGUI.DrawLine();
                Prop(_CustomRim3rdMaskTex, Loc("label_mask"));
                DrawNoise(_CustomRim3rdNoiseStrength, _CustomRim3rdNoiseST);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndVertical();
        }

        // -- Shared Noise Mask --
        void DrawNoiseMask()
        {
            _foldNoise = Foldout(Loc("foldout_noise"), _foldNoise);
            DrawSectionMenu(new[] { _CustomNoiseMaskTex });
            if (!_foldNoise) return;

            EditorGUILayout.BeginVertical(boxOuter);
            EditorGUILayout.BeginVertical(boxInner);
            EditorGUILayout.HelpBox(Loc("help_noise"), MessageType.None);
            Prop(_CustomNoiseMaskTex, Loc("label_noise_texture"));
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndVertical();
        }

        // SpecularExV2 materials among the inspected targets. The list is reused by every OnGUI.
        List<Material> PackingMaterials()
        {
            var mats = _packingMats;
            mats.Clear();
            foreach (var t in m_MaterialEditor.targets)
                if (t is Material mm && SpecularExMaskPacker.HasPackedSlot(mm)) mats.Add(mm);
            return mats;
        }

        // -- Packed mask status line (always visible) --
        // The watcher only keeps the materials in the open scenes up to date, so the state and the
        // manual repair are kept in sight instead of inside the foldout.
        void DrawPackingStatusLine()
        {
            var mats = PackingMaterials();
            if (mats.Count == 0) return;

            // The worst state of the selection.
            var worst = SpecularExPackedMaskStore.PackState.NoMasks;
            foreach (var m in mats)
            {
                var state = SpecularExPackedMaskStore.GetState(m, out _);
                if (Severity(state) > Severity(worst)) worst = state;
            }

            if (_packingLineStyle == null)
                _packingLineStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };

            EditorGUILayout.BeginHorizontal();
            var prevColor = GUI.contentColor;
            if (worst == SpecularExPackedMaskStore.PackState.Pending) GUI.contentColor = new Color(1f, 0.72f, 0.3f);
            GUILayout.Label(Loc("label_packed_masks") + ": " + StateLabel(worst), _packingLineStyle);
            GUI.contentColor = prevColor;
            if (GUILayout.Button(new GUIContent(Loc("btn_rebake"), Loc("tooltip_rebake")), EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
            {
                // Deferred out of OnGUI because it imports assets.
                var selected = mats.ToArray();
                EditorApplication.delayCall += () => SpecularExPackedMaskWatcher.Repair(selected, rebake: true);
            }
            EditorGUILayout.EndHorizontal();
        }

        static int Severity(SpecularExPackedMaskStore.PackState state)
        {
            switch (state)
            {
                case SpecularExPackedMaskStore.PackState.Pending:  return 3;
                case SpecularExPackedMaskStore.PackState.Unsaved:  return 2;
                case SpecularExPackedMaskStore.PackState.UpToDate: return 1;
                default:                                            return 0;
            }
        }

        // -- Mask Packing Status (details) --
        void DrawPackingStatus()
        {
            _foldPacking = Foldout(Loc("foldout_packing"), _foldPacking);
            if (!_foldPacking) return;

            EditorGUILayout.BeginVertical(boxOuter);
            EditorGUILayout.BeginVertical(boxInner);
            EditorGUILayout.HelpBox(Loc("help_packing"), MessageType.None);

            var mats = PackingMaterials();
            foreach (var m in mats)
            {
                var state = SpecularExPackedMaskStore.GetState(m, out string fingerprint);
                if (mats.Count > 1) EditorGUILayout.LabelField(m.name, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(Loc("label_pack_state"), StateLabel(state));
                if (fingerprint != null)
                    EditorGUILayout.SelectableLabel(fingerprint, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(Loc("label_packed_texture") + " 1", m.GetTexture(SpecularExMaskPacker.PackedProp1), typeof(Texture), false);
                    if (m.HasProperty(SpecularExMaskPacker.PackedProp2))
                        EditorGUILayout.ObjectField(Loc("label_packed_texture") + " 2", m.GetTexture(SpecularExMaskPacker.PackedProp2), typeof(Texture), false);
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndVertical();
        }

        static string StateLabel(SpecularExPackedMaskStore.PackState state)
        {
            switch (state)
            {
                case SpecularExPackedMaskStore.PackState.NoMasks:  return Loc("pack_no_masks");
                case SpecularExPackedMaskStore.PackState.UpToDate: return Loc("pack_up_to_date");
                case SpecularExPackedMaskStore.PackState.Pending:  return Loc("pack_pending");
                default:                                            return Loc("pack_unsaved");
            }
        }

        // ========================================================================
        //  Render-mode shader mapping
        // ========================================================================
        // Shipped: Opaque / Cutout / Transparent (normal, one-pass, two-pass), each with an Outline
        // version, and the same set for Tessellation. Every other lilToon variant (Lite, Multi, OutlineOnly,
        // Refraction, Fur, Gem, FakeShadow, Overlay) is not shipped; lilToon's render-mode UI still offers
        // them, so they map to the nearest shipped shader instead of a missing (null) one.
        protected override void ReplaceToCustomShaders()
        {
            lts         = Shader.Find(shaderName + "/lilToon");
            ltsc        = Shader.Find("Hidden/" + shaderName + "/Cutout");
            ltst        = Shader.Find("Hidden/" + shaderName + "/Transparent");
            ltsot       = Shader.Find("Hidden/" + shaderName + "/OnePassTransparent");
            ltstt       = Shader.Find("Hidden/" + shaderName + "/TwoPassTransparent");

            ltso        = Shader.Find("Hidden/" + shaderName + "/OpaqueOutline");
            ltsco       = Shader.Find("Hidden/" + shaderName + "/CutoutOutline");
            ltsto       = Shader.Find("Hidden/" + shaderName + "/TransparentOutline");
            ltsoto      = Shader.Find("Hidden/" + shaderName + "/OnePassTransparentOutline");
            ltstto      = Shader.Find("Hidden/" + shaderName + "/TwoPassTransparentOutline");

            ltstess     = Shader.Find("Hidden/" + shaderName + "/Tessellation/Opaque");
            ltstessc    = Shader.Find("Hidden/" + shaderName + "/Tessellation/Cutout");
            ltstesst    = Shader.Find("Hidden/" + shaderName + "/Tessellation/Transparent");
            ltstessot   = Shader.Find("Hidden/" + shaderName + "/Tessellation/OnePassTransparent");
            ltstesstt   = Shader.Find("Hidden/" + shaderName + "/Tessellation/TwoPassTransparent");

            ltstesso    = Shader.Find("Hidden/" + shaderName + "/Tessellation/OpaqueOutline");
            ltstessco   = Shader.Find("Hidden/" + shaderName + "/Tessellation/CutoutOutline");
            ltstessto   = Shader.Find("Hidden/" + shaderName + "/Tessellation/TransparentOutline");
            ltstessoto  = Shader.Find("Hidden/" + shaderName + "/Tessellation/OnePassTransparentOutline");
            ltstesstto  = Shader.Find("Hidden/" + shaderName + "/Tessellation/TwoPassTransparentOutline");

            // Not shipped: nearest shipped equivalent.
            ltsoo       = ltso;
            ltscoo      = ltsco;
            ltstoo      = ltsto;

            ltsl        = lts;
            ltslc       = ltsc;
            ltslt       = ltst;
            ltslot      = ltsot;
            ltsltt      = ltstt;
            ltslo       = ltso;
            ltslco      = ltsco;
            ltslto      = ltsto;
            ltsloto     = ltsoto;
            ltsltto     = ltstto;

            ltsref      = ltst;
            ltsrefb     = ltst;
            ltsfur      = ltst;
            ltsfurc     = ltsc;
            ltsfurtwo   = ltstt;
            ltsfuro     = ltst;
            ltsfuroc    = ltsc;
            ltsfurotwo  = ltstt;
            ltsgem      = ltst;
            ltsfs       = lts;

            ltsover     = ltst;
            ltsoover    = ltsot;
            ltslover    = ltst;
            ltsloover   = ltsot;

            ltsm        = lts;
            ltsmo       = ltso;
            ltsmref     = ltst;
            ltsmfur     = ltst;
            ltsmgem     = ltst;
        }
    }
}
#endif
