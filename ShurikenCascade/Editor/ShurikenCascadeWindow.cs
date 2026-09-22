using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShurikenCascade
{
    public sealed class ShurikenCascadeWindow : EditorWindow
    {
        [SerializeField] CascadeSession session = new CascadeSession();
        [SerializeField] ParticleSystem selected;
        [SerializeField] List<ParticleSystem> selectedEmitters = new List<ParticleSystem>();
        [SerializeField] ParticleSystem selectionAnchor;
        internal IReadOnlyList<ParticleSystem> SelectedEmitters => selectedEmitters;
        [SerializeField] int moduleIndex = 1;
        [SerializeField] Vector2 columnsScroll;
        [SerializeField] Vector2 savedOrbit = new Vector2(25, -25);
        [SerializeField] Vector3 savedPivot;
        [SerializeField] float savedDistance = 8, savedSpeed = 1;
        [SerializeField] bool savedPlaying = true;
        [SerializeField] Color savedBackground = new Color(0.09f, 0.1f, 0.12f, 1);
        [SerializeField] bool savedPostProcessing;
        [SerializeField] float savedVolumeWeight = 1;
        [SerializeField] VolumeProfile savedVolumeProfile;
        [SerializeField] string[] hiddenKeys = Array.Empty<string>();
        [SerializeField] string soloKey;
        [SerializeField] CascadeTimelineState timelineState = new CascadeTimelineState();
        [SerializeField] bool performanceExpanded;
        [SerializeField] float performanceHeight = 220;
        [SerializeField] float previewRatio = 0.35f;
        internal CascadePanelLayout PanelLayout { get; private set; }
        internal Vector2 GuiScreenOrigin { get; private set; }
        int activeDivider = -1, dividerControl;
        Vector2 dividerStart;
        CascadePanelLayout dividerLayout;
        float originalRatio, originalTrack, originalStats;
        sealed class EmitterColumn : IDisposable
        {
            internal readonly CascadeNativeInspector Inspector;
            internal EmitterColumn(CascadeNativeChangeTracker changes) { Inspector = new CascadeNativeInspector(changes); }
            internal Vector2 Scroll;
            internal int LastVisible;
            public void Dispose() => Inspector.Dispose();
        }
        readonly Dictionary<ParticleSystem, EmitterColumn> columns = new Dictionary<ParticleSystem, EmitterColumn>();
        static readonly string[] moduleNames = CascadeModules.All.Select(module => module.Name).ToArray();
        const float ColumnWidth = 400;
        readonly CascadeNativeChangeTracker nativeChanges = new CascadeNativeChangeTracker();
        ParticleSystem[] emitters = Array.Empty<ParticleSystem>();
        int firstVisible, lastVisible = -1, layoutGeneration;
        bool nativeGuiChanged;
        double nextRepaint;
        Rect previewRect;
        internal int DrawnInspectorCount { get; private set; }
        ParticleSystem lastColumnSelection;
        double nextNativeCheck;
        CascadeTimeline timeline;
        internal CascadeTimeline Timeline => timeline;
        CascadeAnalysisPanel analysis;
        internal CascadeAnalysisPanel Analysis => analysis;
        bool metadataDirty;
        CascadePreview preview;
        internal CascadePreview Preview => preview;
        readonly CascadePreviewGizmos gizmos = new CascadePreviewGizmos();
        internal CascadePreviewGizmos Gizmos => gizmos;
        CascadeModuleClipboard clipboard;
        double lastUpdate;
        bool rebuild;
        string message;

        [MenuItem("Tools/VFX/Shuriken Cascade")]
        public static void ShowWindow()
        {
            var window = GetWindow<ShurikenCascadeWindow>();
            window.titleContent = new GUIContent("Shuriken Cascade");
            window.Show();
        }

        [MenuItem("Assets/Open in Shuriken Cascade", false, 2000)]
        static void OpenSelected()
        {
            ShowWindow();
            GetWindow<ShurikenCascadeWindow>().Open(Selection.activeObject as GameObject);
        }

        [MenuItem("Assets/Open in Shuriken Cascade", true)]
        static bool CanOpenSelected() => CascadeSession.ValidateAsset(Selection.activeObject as GameObject) == null;

        void OnEnable()
        {
            minSize = new Vector2(850, 550);
            titleContent = new GUIContent("Shuriken Cascade");
            saveChangesMessage = "Shuriken Cascade 有未保存的 Prefab 修改。";
            if (session == null) session = new CascadeSession();
            clipboard = new CascadeModuleClipboard();
            previewRatio = EditorPrefs.GetFloat("ShurikenCascade.PreviewRatio", previewRatio);
            performanceExpanded = EditorPrefs.GetBool("ShurikenCascade.PerformanceExpanded", performanceExpanded);
            performanceHeight = EditorPrefs.GetFloat("ShurikenCascade.PerformanceHeight", performanceHeight);
            if (timelineState == null) timelineState = new CascadeTimelineState();
            timelineState.Expanded = EditorPrefs.GetBool("ShurikenCascade.TimelineExpanded", timelineState.Expanded);
            timelineState.Height = EditorPrefs.GetFloat("ShurikenCascade.TimelineHeight", timelineState.Height);
            timeline = new CascadeTimeline();
            analysis = new CascadeAnalysisPanel { Tab = EditorPrefs.GetInt("ShurikenCascade.AnalysisTab", 0) };
            preview = new CascadePreview { Orbit = savedOrbit, Pivot = savedPivot, Distance = savedDistance, Speed = savedSpeed, Playing = savedPlaying, Solo = soloKey };
            if (ColorUtility.TryParseHtmlString(EditorPrefs.GetString("ShurikenCascade.Background", "#" + ColorUtility.ToHtmlStringRGBA(savedBackground)), out var background)) savedBackground = background;
            preview.Background = savedBackground;
            savedPostProcessing = EditorPrefs.GetBool("ShurikenCascade.PostProcessing", savedPostProcessing);
            savedVolumeWeight = EditorPrefs.GetFloat("ShurikenCascade.VolumeWeight", savedVolumeWeight);
            string profileGuid = EditorPrefs.GetString("ShurikenCascade.VolumeProfile", "");
            if (!string.IsNullOrEmpty(profileGuid)) savedVolumeProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(AssetDatabase.GUIDToAssetPath(profileGuid));
            preview.Environment.Enabled = savedPostProcessing; preview.Environment.Weight = savedVolumeWeight;
            if (savedVolumeProfile) preview.Environment.SetSource(savedVolumeProfile);
            foreach (string key in hiddenKeys) preview.Hidden.Add(key);
            try { session.RestoreRecovery(); }
            catch (Exception ex) { message = ex.Message; }
            emitters = session.Emitters;
            NormalizeSelection();
            if (session.Root) nativeChanges.Bind(session);
            rebuild = session.Root;
            timeline.Rebuild(session);
            timelineState.Apply(preview);
            SyncDirty();
            lastUpdate = EditorApplication.timeSinceStartup;
            EditorApplication.update += UpdatePreview;
            Undo.undoRedoPerformed += OnUndoRedo;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.projectChanged += OnProjectChanged;
        }

        void OnDisable()
        {
            if (analysis != null) { EditorPrefs.SetInt("ShurikenCascade.AnalysisTab", analysis.Tab); analysis.Dispose(); }
            gizmos.Dispose();
            timeline?.Parameters.Native.Clear();
            timeline?.CancelEdit();
            ReleaseDivider();
            ObserveNativeChanges();
            DisposeColumns(); nativeChanges.Dispose();
            EditorPrefs.SetFloat("ShurikenCascade.PreviewRatio", previewRatio);
            EditorPrefs.SetBool("ShurikenCascade.PerformanceExpanded", performanceExpanded);
            EditorPrefs.SetFloat("ShurikenCascade.PerformanceHeight", performanceHeight);
            if (timelineState != null)
            {
                EditorPrefs.SetBool("ShurikenCascade.TimelineExpanded", timelineState.Expanded);
                EditorPrefs.SetFloat("ShurikenCascade.TimelineHeight", timelineState.Height);
            }
            clipboard?.Dispose();
            clipboard = null;
            EditorApplication.update -= UpdatePreview;
            Undo.undoRedoPerformed -= OnUndoRedo;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            EditorApplication.projectChanged -= OnProjectChanged;
            if (preview != null)
            {
                savedBackground = preview.Background; savedPostProcessing = preview.Environment.Enabled;
                savedVolumeWeight = preview.Environment.Weight; savedVolumeProfile = preview.Environment.Source;
                EditorPrefs.SetString("ShurikenCascade.Background", "#" + ColorUtility.ToHtmlStringRGBA(savedBackground));
                EditorPrefs.SetBool("ShurikenCascade.PostProcessing", savedPostProcessing);
                EditorPrefs.SetFloat("ShurikenCascade.VolumeWeight", savedVolumeWeight);
                EditorPrefs.SetString("ShurikenCascade.VolumeProfile", savedVolumeProfile ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(savedVolumeProfile)) : "");
                savedOrbit = preview.Orbit; savedPivot = preview.Pivot; savedDistance = preview.Distance;
                savedSpeed = preview.Speed; savedPlaying = preview.Playing;
                hiddenKeys = preview.Hidden.ToArray(); soloKey = preview.Solo;
                preview.Dispose(); preview = null;
            }
        }

        void OnLostFocus() { gizmos.Cancel(); ReleaseDivider(); timeline?.CancelEdit(); Repaint(); }

        void OnDestroy() { session?.Close(); }

        void BeforeReload()
        {
            analysis?.Shaders.Cancel();
            gizmos.Dispose();
            timeline?.Parameters.Native.Clear();
            timeline?.CancelEdit();
            ObserveNativeChanges();
            DisposeColumns();
            preview?.CancelSeek(false);
            try { session.WriteRecovery(); }
            catch (Exception ex) { Debug.LogError("Shuriken Cascade 快照失败：" + ex.Message); }
        }

        void OnProjectChanged() { metadataDirty = true; analysis?.Invalidate(); }

        void UpdatePreview()
        {
            if (preview == null) return;
            double now = EditorApplication.timeSinceStartup;
            float delta = (float)(now - lastUpdate);
            lastUpdate = now;
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (now >= nextNativeCheck)
            {
                nextNativeCheck = now + 0.1;
                ObserveNativeChanges(false);
                preview.RefreshEnvironment();
                if (preview.Ready && preview.PreviewQualityName == null)
                {
                    var names = QualitySettings.names;
                    int index = QualitySettings.GetQualityLevel();
                    string quality = index >= 0 && index < names.Length ? names[index] : null;
                    if (preview.AppliedQualityName != quality) rebuild = true;
                }
            }
            if (rebuild)
            {
                preview.Rebuild(session.Root);
                timeline.Rebuild(session);
                rebuild = false;
            }
            if (metadataDirty) { preview.Statistics.RefreshMetadata(); preview.InvalidateRender(); metadataDirty = false; }
            if (analysis.Update(session.Root, preview.Environment)) Repaint();
            timelineState.Apply(preview);
            preview.Tick(delta);
            preview.Statistics.RefreshSnapshot();
            // Simulation remains fixed at 60 Hz; only scheduled UI painting is throttled.
            double repaintInterval = preview.Playing || preview.IsSeeking ? 1.0 / 30 : 0.1;
            if (session.Root && now >= nextRepaint && (preview.Playing || preview.IsSeeking || focusedWindow == this))
            { nextRepaint = now + repaintInterval; Repaint(); }
        }

        void OnUndoRedo()
        {
            gizmos.Cancel();
            timeline?.Parameters.Native.Clear();
            timeline?.CancelEdit();
            if (!session.Root || !session.CheckUndo()) return;
            DisposeColumns();
            if (!selected) selected = session.Emitters.FirstOrDefault();
            preview?.Hidden.Clear();
            if (preview != null) preview.Solo = null;
            Changed();
        }

        void Changed(bool observed = false)
        {
            analysis?.Invalidate();
            gizmos.Cancel();
            timeline?.Parameters.Native.Clear();
            timeline?.CancelEdit();
            if (!observed) { session.MarkDirty(); RefreshNativeBaselines(); }
            emitters = session.Emitters;
            timeline?.Rebuild(session); // Authored hit targets must match edits/Undo before the next input event.
            NormalizeSelection();
            preview?.CancelSeek(false);
            rebuild = true;
            SyncDirty();
            Repaint();
        }

        void SyncDirty() { hasUnsavedChanges = session != null && session.Dirty; }

        bool ResolveUnsaved()
        {
            ObserveNativeChanges();
            if (!session.Dirty) return true;
            int choice = EditorUtility.DisplayDialogComplex("未保存的 Prefab", "保存当前修改后再继续？", "保存", "取消", "放弃");
            if (choice == 1) return false;
            return choice == 2 || TrySave();
        }

        public void Open(GameObject asset)
        {
            string error = CascadeSession.ValidateAsset(asset);
            if (error != null) { message = error; return; }
            if (!ResolveUnsaved()) return;
            gizmos.Cancel();
            Run(() =>
            {
                DisposeColumns();
                session.Open(asset);
                analysis.Bind(session.Root);
                emitters = session.Emitters; nativeChanges.Bind(session);
                SelectEmitter(emitters.FirstOrDefault(), false);
                preview.Hidden.Clear(); preview.Solo = null;
                timelineState.ResetRange(); timelineState.Apply(preview);
                preview.Rebuild(session.Root); preview.Frame();
                timeline.Rebuild(session);
                rebuild = false;
                columnsScroll = Vector2.zero; lastColumnSelection = null;
                SyncDirty();
            });
        }

        bool TrySave()
        {
            ObserveNativeChanges();
            try { session.Save(); RefreshNativeBaselines(); SyncDirty(); message = null; return true; }
            catch (Exception ex) { message = ex.Message; Repaint(); return false; }
        }

        public override void SaveChanges()
        {
            if (!TrySave())
            {
                EditorUtility.DisplayDialog("保存失败", message, "保留编辑");
                // Do not acknowledge the native window-close save request on failure.
                throw new InvalidOperationException(message);
            }
            base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            gizmos.Dispose();
            DisposeColumns();
            preview?.CancelSeek();
            nativeChanges.Dispose(); emitters = Array.Empty<ParticleSystem>();
            selectedEmitters.Clear(); selected = selectionAnchor = null;
            session.Close();
            analysis?.Bind(null);
            preview?.Rebuild(null);
            timeline?.Rebuild(session);
            base.DiscardChanges();
        }

        void Run(Action action)
        {
            try { action(); message = null; }
            catch (Exception ex) { message = ex.Message; }
        }

        void DisposeColumns()
        {
            foreach (var column in columns.Values) column.Dispose();
            columns.Clear();
        }

        void RefreshNativeBaselines() => nativeChanges.RefreshBaseline();

        void ObserveNativeChanges(bool force = true)
        {
            if (nativeChanges.ObserveChanges(force)) Changed(true);
        }

        void OnGUI()
        {
            gizmos.HandleEarlyInput(selected);
            if (EditorApplication.isPlayingOrWillChangePlaymode) gizmos.Cancel();
            GuiScreenOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero);
            timeline?.Parameters.DrawNativeFields(session, GuiScreenOrigin, () => Changed());
            // Stable handle IDs precede native inspectors, whose control count varies by event.
            if (!EditorApplication.isPlayingOrWillChangePlaymode && session.Root && preview != null)
            {
                gizmos.Draw(previewRect, position.size, preview.HandleCameraObject, session, selected, () => Changed());
                preview.HandleCamera(previewRect);
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode) timeline?.CancelEdit();
            if (timeline != null && timeline.IsEditing)
            {
                Run(() => timeline.HandleDrag(() => Changed()));
                if (Event.current.type == EventType.Used) Repaint();
            }
            timeline?.Parameters.HandleKeyboard(session, () => { if (this) Changed(); });
            using (new GUILayout.AreaScope(new Rect(0, 0, position.width, 22))) Toolbar();
            HandleDrop();
            Event e = Event.current;
            if (e.type == EventType.KeyDown && (e.control || e.command) && e.keyCode == KeyCode.S)
            { TrySave(); e.Use(); }
            float y = 22;
            if (!string.IsNullOrEmpty(message))
            {
                float height = EditorStyles.helpBox.CalcHeight(new GUIContent(message), Mathf.Max(1, position.width - 8)) + 8;
                EditorGUI.HelpBox(new Rect(4, y + 4, position.width - 8, height - 8), message, MessageType.Warning);
                y += height;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { DisposeColumns(); GUI.Label(new Rect(8, y, position.width - 16, 40), "请退出 Play Mode 后编辑与预览。"); return; }
            if (!session.Root)
            {
                GUI.Label(new Rect(0, y, position.width, position.height - y), "拖入特效 Prefab 开始编辑\n独立预览 / 发射器原生 Inspector / 保存回 Prefab",
                    new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter });
                return;
            }
            NormalizeSelection();
            PanelLayout = new CascadePanelLayout(new Rect(0, y, position.width, Mathf.Max(0, position.height - y)),
                previewRatio, timelineState.Height, performanceHeight, timelineState.Expanded, performanceExpanded);
            HandleDivider(0, PanelLayout.HorizontalGrip, true);
            HandleDivider(1, PanelLayout.TimelineGrip, false);
            HandleDivider(2, PanelLayout.PerformanceGrip, false);
            using (new GUILayout.AreaScope(PanelLayout.Preview))
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    if (GUILayout.Button(preview.Playing ? "暂停" : "播放", EditorStyles.toolbarButton)) preview.TogglePlaying();
                    if (GUILayout.Button("重播", EditorStyles.toolbarButton)) preview.Restart();
                    if (GUILayout.Button("聚焦", EditorStyles.toolbarButton)) preview.Frame();
                    float[] speeds = { 0.25f, 0.5f, 1f, 2f, 4f };
                    int speed = Mathf.Max(0, Array.IndexOf(speeds, preview.Speed));
                    preview.Speed = speeds[EditorGUILayout.Popup(speed, new[] { "0.25×", "0.5×", "1×", "2×", "4×" }, EditorStyles.toolbarPopup, GUILayout.Width(55))];
                }
                gizmos.Toolbar();
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    GUILayout.Label("背景", GUILayout.Width(28));
                    EditorGUI.BeginChangeCheck();
                    preview.Background = EditorGUILayout.ColorField(GUIContent.none, preview.Background, false, false, false, GUILayout.Width(42));
                    preview.Environment.Enabled = GUILayout.Toggle(preview.Environment.Enabled, "后处理", EditorStyles.toolbarButton, GUILayout.Width(48));
                    if (EditorGUI.EndChangeCheck()) { preview.Environment.NotifyChanged(); preview.InvalidateRender(); }
                    if (GUILayout.Button("环境设置…", EditorStyles.toolbarButton)) CascadeEnvironmentWindow.Open(this, preview);
                }
                Rect rect = new Rect(0, 66, PanelLayout.Preview.width, Mathf.Max(1, PanelLayout.TopHeight - 66));
                previewRect = new Rect(PanelLayout.Preview.x, PanelLayout.Preview.y + 66, rect.width, rect.height);
                preview.Draw(rect, false);
            }
            using (new GUILayout.AreaScope(PanelLayout.Columns)) DrawColumns(PanelLayout.TopHeight, PanelLayout.Columns.width);
            bool expanded = timelineState.Expanded;
            using (new GUILayout.AreaScope(PanelLayout.Timeline)) timeline.Draw(timelineState, session, preview, ref selected, PanelLayout.TrackHeight, SelectFromList, IsEmitterSelected, () => { if (this) Changed(); }, clipboard);
            if (expanded != timelineState.Expanded) { Repaint(); GUIUtility.ExitGUI(); }
            using (new GUILayout.AreaScope(PanelLayout.Performance))
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    bool next = EditorGUILayout.Foldout(performanceExpanded, "分析", true);
                    int oldTab = analysis.Tab;
                    analysis.DrawHeader();
                    if (oldTab != analysis.Tab) { performanceExpanded = true; Repaint(); GUIUtility.ExitGUI(); }
                    if (next != performanceExpanded) { performanceExpanded = next; Repaint(); GUIUtility.ExitGUI(); }
                }
                if (performanceExpanded)
                    using (new EditorGUILayout.VerticalScope(GUILayout.Height(PanelLayout.StatsHeight))) analysis.Draw(session, preview, gizmos, ref selected, SelectFromList, IsEmitterSelected);
            }
            gizmos.Composite(previewRect, position.size);
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Delete && !EditorGUIUtility.editingTextField &&
                GUIUtility.keyboardControl == 0 && GUIUtility.hotControl == 0)
            { DeleteSelectedEmitters(); e.Use(); GUIUtility.ExitGUI(); }

        }

        void NormalizeSelection()
        {
            if (selectedEmitters == null) selectedEmitters = new List<ParticleSystem>();
            selectedEmitters.RemoveAll(emitter => !emitter || Array.IndexOf(emitters, emitter) < 0);
            if (!selected || Array.IndexOf(emitters, selected) < 0) selected = selectedEmitters.FirstOrDefault() ?? emitters.FirstOrDefault();
            if (!selectionAnchor || Array.IndexOf(emitters, selectionAnchor) < 0) selectionAnchor = selected;
        }

        internal bool IsEmitterSelected(ParticleSystem emitter) => selectedEmitters.Contains(emitter);

        internal void SelectEmitter(ParticleSystem emitter, bool range, bool reveal = true)
        {
            int end = Array.IndexOf(emitters, emitter);
            if (emitter && end < 0) return;
            int anchor = Array.IndexOf(emitters, selectionAnchor);
            selectedEmitters.Clear();
            if (emitter)
            {
                if (range && anchor >= 0)
                    for (int i = Math.Min(anchor, end); i <= Math.Max(anchor, end); i++) selectedEmitters.Add(emitters[i]);
                else { selectedEmitters.Add(emitter); selectionAnchor = emitter; }
            }
            else selectionAnchor = null;
            selected = emitter;
            if (!reveal) lastColumnSelection = emitter;
            if (Event.current != null) GUI.FocusControl(null);
            Repaint();
        }

        void ToggleEmitterSelection(ParticleSystem emitter, bool value, bool range)
        {
            if (range) { SelectEmitter(emitter, true, false); return; }
            if (value) { if (!selectedEmitters.Contains(emitter)) selectedEmitters.Add(emitter); }
            else selectedEmitters.Remove(emitter);
            selected = selectionAnchor = lastColumnSelection = emitter;
            GUI.FocusControl(null); Repaint();
        }

        void SelectFromList(ParticleSystem emitter) => SelectEmitter(emitter, Event.current.shift);

        internal void DeleteSelectedEmitters()
        {
            NormalizeSelection();
            if (selectedEmitters.Count == 0) return;
            Run(() =>
            {
                ObserveNativeChanges();
                int nextIndex = Math.Max(0, Array.IndexOf(emitters, selected));
                var targets = selectedEmitters.ToArray();
                DisposeColumns();
                session.DeleteMany(targets);
                emitters = session.Emitters;
                SelectEmitter(emitters.Length > 0 ? emitters[Math.Min(nextIndex, emitters.Length - 1)] : null, false);
                StructureChanged();
            });
            Repaint();
        }

        void HandleDivider(int index, Rect grip, bool horizontal)
        {
            int control = GUIUtility.GetControlID(0x43CA + index, FocusType.Passive);
            if (grip.width <= 0 || grip.height <= 0) return;
            Event e = Event.current;
            EditorGUIUtility.AddCursorRect(grip, horizontal ? MouseCursor.ResizeHorizontal : MouseCursor.ResizeVertical);
            if (e.type == EventType.Repaint)
            {
                bool highlight = grip.Contains(e.mousePosition) || activeDivider == index;
                EditorGUI.DrawRect(grip, highlight ? new Color(0.28f, 0.48f, 0.65f) : new Color(0.20f, 0.20f, 0.20f));
            }
            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition))
            {
                GUI.FocusControl(null);
                activeDivider = index; dividerControl = control; GUIUtility.hotControl = control;
                dividerStart = e.mousePosition; dividerLayout = PanelLayout;
                originalRatio = previewRatio; originalTrack = timelineState.Height; originalStats = performanceHeight;
                e.Use();
            }
            if (activeDivider != index || GUIUtility.hotControl != dividerControl) return;
            if (e.type == EventType.MouseDrag)
            {
                float delta = horizontal ? e.mousePosition.x - dividerStart.x : e.mousePosition.y - dividerStart.y;
                dividerLayout.Resize(index, delta, ref previewRatio, ref timelineState.Height, ref performanceHeight);
                e.Use(); Repaint();
            }
            if (e.type == EventType.MouseUp && e.button == 0) { ReleaseDivider(); e.Use(); Repaint(); }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                previewRatio = originalRatio; timelineState.Height = originalTrack; performanceHeight = originalStats;
                ReleaseDivider(); e.Use(); Repaint();
            }
        }

        void ReleaseDivider()
        {
            if (activeDivider >= 0 && GUIUtility.hotControl == dividerControl) GUIUtility.hotControl = 0;
            activeDivider = -1;
        }

        void ResetLayout()
        {
            ReleaseDivider(); previewRatio = 0.35f; timelineState.Height = 170; performanceHeight = 220;
            timelineState.Expanded = true; performanceExpanded = false; Repaint();
        }

        void Toolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var current = string.IsNullOrEmpty(session.Path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(session.Path);
                var next = EditorGUILayout.ObjectField(current, typeof(GameObject), false, GUILayout.MinWidth(160));
                if (next != current && next) Open(next as GameObject);
                using (new EditorGUI.DisabledScope(!session.Root))
                {
                    if (GUILayout.Button("保存", EditorStyles.toolbarButton, GUILayout.Width(45))) TrySave();
                    if (GUILayout.Button("Distance LOD", EditorStyles.toolbarButton, GUILayout.Width(95)))
                        CascadeLODPopup.Open(session, () => { if (this) Changed(); });
                    var lod = session.Root ? session.Root.GetComponent<ParticleDistanceLOD>() : null;
                    if (lod)
                    {
                        var qualityNames = new[] { "画质：跟随项目" }.Concat(QualitySettings.names).ToArray();
                        int currentQuality = Array.IndexOf(qualityNames, preview.PreviewQualityName);
                        currentQuality = Mathf.Max(0, currentQuality);
                        int nextQuality = EditorGUILayout.Popup(currentQuality, qualityNames, EditorStyles.toolbarPopup, GUILayout.Width(120));
                        if (nextQuality != currentQuality)
                        {
                            preview.PreviewQualityName = nextQuality == 0 ? null : qualityNames[nextQuality];
                            rebuild = true;
                        }
                    }
                    if (lod && lod.levels != null && lod.levels.Length > 0)
                    {
                        var labels = Enumerable.Range(0, lod.levels.Length).Select(i => "LOD " + i).ToArray();
                        int nextLOD = EditorGUILayout.Popup(Mathf.Clamp(preview.PreviewLOD, 0, labels.Length - 1), labels, EditorStyles.toolbarPopup, GUILayout.Width(65));
                        if (nextLOD != preview.PreviewLOD) { preview.PreviewLOD = nextLOD; rebuild = true; }
                    }
                    if (GUILayout.Button("还原", EditorStyles.toolbarButton, GUILayout.Width(45)))
                        if (!session.Dirty || EditorUtility.DisplayDialog("还原 Prefab", "放弃当前编辑并重新读取源 Prefab？", "还原", "取消"))
                            Run(() => { DisposeColumns(); session.Reload(); emitters = session.Emitters; nativeChanges.Bind(session); SelectEmitter(emitters.FirstOrDefault(), false); preview.Hidden.Clear(); preview.Solo = null; rebuild = true; SyncDirty(); });
                }
                GUILayout.Label(session.Dirty ? "● 未保存" : "已保存", EditorStyles.miniLabel, GUILayout.Width(65));
                if (GUILayout.Button("重置布局", EditorStyles.toolbarButton, GUILayout.Width(65))) { ResetLayout(); GUIUtility.ExitGUI(); }
            }
        }

        internal static Vector2Int VisibleColumns(float offset, float width, int count)
        {
            if (count == 0) return new Vector2Int(0, -1);
            const float stride = ColumnWidth + 4;
            int first = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0, offset) / stride), 0, count - 1);
            int last = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(0, offset) + Mathf.Max(1, width)) / stride) - 1, first, count - 1);
            return new Vector2Int(first, last);
        }

        void DrawColumns(float height, float width)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(width)))
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    GUILayout.Label("发射器 (" + emitters.Length + ") · 已选 " + selectedEmitters.Count, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(selectedEmitters.Count == 0))
                        if (GUILayout.Button(new GUIContent("删除选中", "直接删除选中发射器及子树；Ctrl+Z 撤销。Shift 点击选择连续范围。"), EditorStyles.toolbarButton, GUILayout.Width(65)))
                        { DeleteSelectedEmitters(); GUIUtility.ExitGUI(); }
                    if (GUILayout.Button("+ 新增", EditorStyles.toolbarButton, GUILayout.Width(60)))
                    {
                        Run(() => { ObserveNativeChanges(); DisposeColumns(); var added = session.Add(); emitters = session.Emitters; SelectEmitter(added, false); StructureChanged(); });
                        GUIUtility.ExitGUI();
                    }
                }
                // Freeze virtualization for a whole Layout/input/Repaint sequence. Changing the set
                // during ScrollWheel or a scrollbar drag breaks GUILayout's control tree.
                if (Event.current.type == EventType.Layout)
                {
                    if (selected != lastColumnSelection)
                    {
                        GUI.FocusControl(null);
                        int index = Array.IndexOf(emitters, selected);
                        if (index >= 0) columnsScroll.x = index * (ColumnWidth + 4);
                        lastColumnSelection = selected;
                    }
                    columnsScroll.x = Mathf.Clamp(columnsScroll.x, 0, Mathf.Max(0, emitters.Length * (ColumnWidth + 4) - width));
                    var range = VisibleColumns(columnsScroll.x, width, emitters.Length);
                    firstVisible = range.x; lastVisible = range.y;
                    layoutGeneration++;
                    TrimInspectorCache();
                }
                Vector2 previousScroll = columnsScroll;
                columnsScroll = GUILayout.BeginScrollView(columnsScroll, true, false,
                    GUI.skin.horizontalScrollbar, GUIStyle.none, GUILayout.Height(height - 22));
                DrawnInspectorCount = 0;
                nativeGuiChanged = false;
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int i = 0; i < emitters.Length; i++)
                    {
                        if (i < firstVisible || i > lastVisible) { GUILayout.Space(ColumnWidth + 4); continue; }
                        DrawColumn(emitters[i], height - 48);
                        DrawnInspectorCount++;
                    }
                    GUILayout.FlexibleSpace();
                }
                GUILayout.EndScrollView();
                if (previousScroll != columnsScroll) { GUI.FocusControl(null); Repaint(); }
            }
            if (Event.current.type != EventType.Layout && Event.current.type != EventType.Repaint)
                ObserveNativeChanges(nativeGuiChanged);
        }

        void TrimInspectorCache()
        {
            // Keep the selected editor alive for delayed popup commits. Offscreen editors have a
            // small grace period so horizontal scrolling does not constantly recreate their curves.
            foreach (var pair in columns)
            {
                int index = Array.IndexOf(emitters, pair.Key);
                if (index >= firstVisible && index <= lastVisible) continue;
                if (pair.Key == selected || GUIUtility.hotControl != 0) continue;
                if (layoutGeneration - pair.Value.LastVisible > 2) pair.Value.Dispose();
            }
        }

        void DrawColumn(ParticleSystem emitter, float height)
        {
            if (!columns.TryGetValue(emitter, out var column))
            {
                column = new EmitterColumn(nativeChanges);
                columns.Add(emitter, column);
            }
            column.LastVisible = layoutGeneration;
            string key = CascadeSession.Key(emitter.transform, session.Root.transform);
            bool readOnly = session.IsReadOnly(emitter);
            using (var scope = new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(ColumnWidth), GUILayout.Height(height)))
            {
                // Keep the active Inspector alive for popups without changing checkbox membership.
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && scope.rect.Contains(Event.current.mousePosition))
                    selected = lastColumnSelection = emitter;
                using (new EditorGUILayout.HorizontalScope())
                {
                    Color old = GUI.backgroundColor;
                    if (IsEmitterSelected(emitter)) GUI.backgroundColor = new Color(0.35f, 0.7f, 1f);
                    bool wasSelected = IsEmitterSelected(emitter);
                    bool isChecked = GUILayout.Toggle(wasSelected, new GUIContent("", "勾选／取消此发射器；Shift 点击选择连续范围。多选仅用于删除。"), EditorStyles.toggle, GUILayout.Width(18));
                    if (isChecked != wasSelected) ToggleEmitterSelection(emitter, isChecked, Event.current.shift);
                    GUI.backgroundColor = old;
                    using (new EditorGUI.DisabledScope(readOnly))
                    {
                        string name = EditorGUILayout.DelayedTextField(emitter.name, EditorStyles.boldLabel);
                        if (!string.IsNullOrWhiteSpace(name) && name != emitter.name)
                        { ObserveNativeChanges(); Undo.RecordObject(emitter.gameObject, "Rename emitter"); emitter.name = name; Changed(); }
                    }
                }
                if (GUILayout.Button(readOnly ? "嵌套 Prefab · 只读" : session.DisplayPath(emitter.transform), EditorStyles.miniLabel)) SelectEmitter(emitter, Event.current.shift, false);
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool visible = !preview.Hidden.Contains(key);
                    bool show = GUILayout.Toggle(visible, "显示", EditorStyles.miniButtonLeft);
                    if (show != visible) { if (show) preview.Hidden.Remove(key); else preview.Hidden.Add(key); preview.ApplyVisibility(); }
                    bool solo = GUILayout.Toggle(preview.Solo == key, "独显", EditorStyles.miniButtonRight);
                    if (solo != (preview.Solo == key)) { preview.Solo = solo ? key : null; preview.ApplyVisibility(); }
                    using (new EditorGUI.DisabledScope(!session.CanChangeSubtree(emitter)))
                    {
                        if (GUILayout.Button("复制发射器", EditorStyles.miniButton))
                        { Run(() => { ObserveNativeChanges(); DisposeColumns(); var copy = session.Duplicate(emitter); emitters = session.Emitters; SelectEmitter(copy, false); StructureChanged(); }); GUIUtility.ExitGUI(); }
                        if (GUILayout.Button("删除", EditorStyles.miniButton))
                        {
                            if (!IsEmitterSelected(emitter)) SelectEmitter(emitter, false, false);
                            DeleteSelectedEmitters();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    moduleIndex = EditorGUILayout.Popup(moduleIndex, moduleNames, EditorStyles.toolbarPopup, GUILayout.MinWidth(80));
                    DrawClipboardButtons(emitter, moduleIndex);
                }
                column.Scroll = EditorGUILayout.BeginScrollView(column.Scroll, GUILayout.ExpandHeight(true));
                EditorGUI.BeginChangeCheck();
                column.Inspector.Draw(session, emitter, false);
                nativeGuiChanged |= EditorGUI.EndChangeCheck();
                EditorGUILayout.EndScrollView();
            }
        }

        void StructureChanged()
        {
            preview.Hidden.Clear(); preview.Solo = null;
            Changed();
        }

        void DrawClipboardButtons(ParticleSystem emitter, int index)
        {
            var module = CascadeModules.All[index];
            using (new EditorGUI.DisabledScope(!module.Supported || !CascadeModuleClipboard.Target(emitter, module.Path)))
                if (GUILayout.Button(new GUIContent("复制模块", "保存当前模块快照，包含启用状态。"), EditorStyles.toolbarButton, GUILayout.Width(65))) CopyModule(emitter, index);
            bool canPaste = clipboard.CanPaste(session, emitter, module, out string reason);
            using (new EditorGUI.DisabledScope(!canPaste))
                if (GUILayout.Button(new GUIContent("粘贴模块", canPaste ? "来自 " + clipboard.Label + "；可撤销。" : reason), EditorStyles.toolbarButton, GUILayout.Width(65))) PasteModule(emitter, index);
        }

        void CopyModule(ParticleSystem emitter, int index)
        {
            Run(() => { ObserveNativeChanges(); clipboard.Copy(emitter, CascadeModules.All[index]); });
            Repaint();
        }

        void PasteModule(ParticleSystem emitter, int index)
        {
            Run(() => { ObserveNativeChanges(); clipboard.Paste(session, emitter, CascadeModules.All[index]); Changed(); });
        }

        internal bool CanOpenPrefabDrop(Vector2 point) => !session.Root || point.y < EditorGUIUtility.singleLineHeight + 5 || previewRect.Contains(point);

        void HandleDrop()
        {
            var e = Event.current;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            var asset = DragAndDrop.objectReferences.OfType<GameObject>().FirstOrDefault(go => EditorUtility.IsPersistent(go));
            if (!asset || CascadeSession.ValidateAsset(asset) != null) return;
            // Never steal prefab/object reference drops from a native Inspector.
            if (!CanOpenPrefabDrop(e.mousePosition)) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); Open(asset); }
            e.Use();
        }
    }
}
