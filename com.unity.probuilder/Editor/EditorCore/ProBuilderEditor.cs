using System;
using UnityEngine;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEditor.ProBuilder.Actions;
using UnityEngine.ProBuilder;
using PMesh = UnityEngine.ProBuilder.ProBuilderMesh;
using UnityEngine.ProBuilder.MeshOperations;
using Math = UnityEngine.ProBuilder.Math;
using Object = UnityEngine.Object;
using RaycastHit = UnityEngine.ProBuilder.RaycastHit;
using UnityEditor.SettingsManagement;

namespace UnityEditor.ProBuilder
{
	/// <summary>
	/// Manages the ProBuilder toolbar window and tool mode.
	/// </summary>
	public sealed class ProBuilderEditor : EditorWindow, IHasCustomMenu
	{
		// Match the value set in RectSelection.cs
		const float k_MouseDragThreshold = 6f;

		/// <value>
		/// Raised any time the ProBuilder editor refreshes the selection. This is called every frame when interacting with mesh elements, and after any mesh operation.
		/// </value>
		public static event Action<ProBuilderMesh[]> selectionUpdated;

		/// <value>
		/// Raised when the EditLevel is changed.
		/// </value>
        public static event Action<SelectMode> selectModeChanged;

		static EditorToolbar s_EditorToolbar;
		static ProBuilderEditor s_Instance;
		EditorMeshHandles m_EditorMeshHandles;

		GUIContent[] m_EditModeIcons;
		GUIStyle VertexTranslationInfoStyle;

		[UserSetting("General", "Show Scene Info", "Toggle the display of information about selected meshes in the Scene View.")]
		static Pref<bool> s_ShowSceneInfo = new Pref<bool>("editor.showSceneInfo", false);

		[UserSetting("Toolbar", "Icon GUI", "Toggles the ProBuilder window interface between text and icon versions.")]
		internal static Pref<bool> s_IsIconGui = new Pref<bool>("editor.toolbarIconGUI", false);

		[UserSetting("Toolbar", "Unique Mode Shortcuts", "When off, the G key toggles between Object and Element modes and H enumerates the element modes.  If on, G, H, J, and K are shortcuts to Object, Vertex, Edge, and Face modes respectively.")]
		internal static Pref<bool> s_UniqueModeShortcuts = new Pref<bool>("editor.uniqueModeShortcuts", false, SettingsScopes.User);

		[UserSetting("Mesh Editing", "Allow non-manifold actions", "Enables advanced mesh editing techniques that may create non-manifold geometry.")]
		internal static Pref<bool> s_AllowNonManifoldActions = new Pref<bool>("editor.allowNonManifoldActions", false, SettingsScopes.User);

		[UserSetting("Toolbar", "Toolbar Location", "Where the Object, Face, Edge, and Vertex toolbar will be shown in the Scene View.")]
		static Pref<SceneToolbarLocation> s_SceneToolbarLocation = new Pref<SceneToolbarLocation>("editor.sceneToolbarLocation", SceneToolbarLocation.UpperCenter, SettingsScopes.User);

		static Pref<bool> s_WindowIsFloating = new Pref<bool>("UnityEngine.ProBuilder.ProBuilderEditor-isUtilityWindow", false, SettingsScopes.Project);

		internal Pref<bool> m_BackfaceSelectEnabled = new Pref<bool>("editor.backFaceSelectEnabled", false);
		internal Pref<RectSelectMode> m_DragSelectRectMode = new Pref<RectSelectMode>("editor.dragSelectRectMode", RectSelectMode.Partial);
		internal Pref<SelectionModifierBehavior> m_SelectModifierBehavior = new Pref<SelectionModifierBehavior>("editor.rectSelectModifier", SelectionModifierBehavior.Difference);
		Pref<SelectMode> m_SelectMode = new Pref<SelectMode>("editor.selectMode", SelectMode.Object);

		internal static Pref<HandleOrientation> s_HandleOrientation = new Pref<HandleOrientation>("editor.handleAlignment", HandleOrientation.World);

		internal RectSelectMode rectSelectMode
		{
			get { return m_DragSelectRectMode.value; }
			set
			{
				if (m_DragSelectRectMode.value == value)
					return;
				m_DragSelectRectMode.SetValue(value, true);
				m_ScenePickerPreferences.rectSelectMode = value;
			}
		}

		internal SelectionModifierBehavior selectionModifierBehavior
		{
			get { return m_SelectModifierBehavior.value; }

			set
			{
				if (m_SelectModifierBehavior.value == value)
					return;
				m_SelectModifierBehavior.SetValue(value, true);
				m_ScenePickerPreferences.selectionModifierBehavior = value;
			}
		}

		internal static HandleOrientation handleOrientation
		{
			get { return s_HandleOrientation.value; }

			set
			{
				if (value == s_HandleOrientation.value)
					return;

				if (selectMode.ContainsFlag(SelectMode.TextureFace))
					value = HandleOrientation.Normal;

				s_HandleOrientation.SetValue(value, true);
			}
		}

		internal bool backfaceSelectionEnabled
		{
			get { return m_BackfaceSelectEnabled.value; }

			set
			{
				if (value == m_BackfaceSelectEnabled.value)
					return;

				m_BackfaceSelectEnabled.SetValue(value, true);
				m_ScenePickerPreferences.cullMode = value ? CullingMode.None : CullingMode.Back;
			}
		}

		// used for 'g' key shortcut to swap between object/vef modes
		SelectMode m_LastComponentMode;
		HandleOrientation m_PreviousHandleOrientation;
		[UserSetting]
		static internal Pref<Shortcut[]> s_Shortcuts = new Pref<Shortcut[]>("editor.sceneViewShortcuts", Shortcut.DefaultShortcuts().ToArray());
		GUIStyle m_CommandStyle;
		Rect m_ElementModeToolbarRect = new Rect(3, 6, 128, 24);

		int m_DefaultControl;
		SceneSelection m_Hovering = new SceneSelection();
		SceneSelection m_HoveringPrevious = new SceneSelection();
		ScenePickerPreferences m_ScenePickerPreferences;

		[UserSetting("Graphics", "Show Hover Highlight", "Highlight the mesh element nearest to the mouse cursor.")]
		static Pref<bool> s_ShowHoverHighlight = new Pref<bool>("editor.showPreselectionHighlight", true, SettingsScopes.User);

		Tool m_CurrentTool = Tool.Move;
		Vector2 m_InitialMousePosition;
		Rect m_MouseDragRect;
		bool m_IsDragging;
		bool m_IsReadyForMouseDrag;
		// prevents leftClickUp from stealing focus after double click
		bool m_WasDoubleClick;
		// vertex handles
		bool m_IsRightMouseDown;

		Vector3[][] m_VertexPositions;
		Vector3[] m_VertexOffset;

		GUIContent m_SceneInfo = new GUIContent();

		Rect m_SceneInfoRect = new Rect(10, 10, 200, 40);

#if !UNITY_2018_2_OR_NEWER
		static MethodInfo s_ResetOnSceneGUIState = null;
#endif

		// All selected pb_Objects
		internal ProBuilderMesh[] selection = new ProBuilderMesh[0];

		Event m_CurrentEvent;

		internal bool isFloatingWindow { get; private set; }

		/// <value>
		/// Get the current @"UnityEngine.ProBuilder.EditLevel".
		/// </value>
		[Obsolete]
		internal static EditLevel editLevel
		{
			get { return s_Instance != null ? EditorUtility.GetEditLevel(instance.m_SelectMode) : EditLevel.Top; }
		}

		/// <summary>
		/// Get the current @"UnityEngine.ProBuilder.SelectMode".
		/// </summary>
		/// <value>The ComponentMode currently set.</value>
		[Obsolete]
		internal static ComponentMode componentMode
		{
			get { return s_Instance != null ? EditorUtility.GetComponentMode(instance.m_SelectMode) : ComponentMode.Face; }
		}

		/// <value>
		/// Get and set the current SelectMode.
		/// </value>
		public static SelectMode selectMode
		{
			get
			{
				if (s_Instance != null)
					return s_Instance.m_SelectMode;

				// for backwards compatibility reasons `Object` is returned when editor is closed
				return SelectMode.Object;
			}

			set
			{
				if (s_Instance == null)
					return;

				var previous = s_Instance.m_SelectMode.value;

				if (previous == value)
					return;

				s_Instance.m_SelectMode.SetValue(value, true);

				if (previous == SelectMode.Edge || previous == SelectMode.Vertex || previous == SelectMode.Face)
					s_Instance.m_LastComponentMode = previous;

				if (value == SelectMode.Object)
					Tools.current = s_Instance.m_CurrentTool;

				if (value == SelectMode.TextureFace)
					s_Instance.m_PreviousHandleOrientation = s_HandleOrientation;

				if (previous == SelectMode.TextureFace)
					handleOrientation = s_Instance.m_PreviousHandleOrientation;

				if (selectModeChanged != null)
					selectModeChanged(value);

				UpdateMeshHandles(true);
				s_Instance.Repaint();
			}
		}

		Stack<SelectMode> m_SelectModeHistory = new Stack<SelectMode>();

		internal static void PushSelectMode(SelectMode mode)
		{
			s_Instance.m_SelectModeHistory.Push(selectMode);
			selectMode = mode;
		}

		internal static void PopSelectMode()
		{
			if (s_Instance.m_SelectModeHistory.Count < 1)
				return;
			selectMode = s_Instance.m_SelectModeHistory.Pop();
		}

		internal static void ResetToLastSelectMode()
		{
			selectMode = s_Instance.m_LastComponentMode;
		}

		static class SceneStyles
		{
			static bool s_Init = false;
			static GUIStyle s_SelectionRect;

			public static GUIStyle selectionRect
			{
				get { return s_SelectionRect; }
			}

			public static void Init()
			{
				if (s_Init)
					return;

				s_Init = true;

				s_SelectionRect = new GUIStyle()
				{
					normal = new GUIStyleState()
					{
						background = IconUtility.GetIcon("Scene/SelectionRect")
					},
					border = new RectOffset(1,1,1,1),
					margin = new RectOffset(0,0,0,0),
					padding = new RectOffset(0,0,0,0)
				};
			}
		}

		/// <value>
		/// Get the active ProBuilderEditor window. Null if no instance is open.
		/// </value>
		public static ProBuilderEditor instance
		{
			get { return s_Instance; }
		}

		internal static void MenuOpenWindow()
		{
			ProBuilderEditor editor = (ProBuilderEditor) EditorWindow.GetWindow(typeof(ProBuilderEditor),
				s_WindowIsFloating, PreferenceKeys.pluginTitle,
				true); // open as floating window
			editor.isFloatingWindow = s_WindowIsFloating;
		}

		void OnBecameVisible()
		{
			// fixes maximizing/unmaximizing
			s_Instance = this;
		}

		void OnEnable()
		{
			s_Instance = this;

			if (m_EditorMeshHandles != null)
				m_EditorMeshHandles.Dispose();

			m_EditorMeshHandles = new EditorMeshHandles();

#if UNITY_2019_1_OR_NEWER
			SceneView.duringSceneGui += OnSceneGUI;
#else
			SceneView.onSceneGUIDelegate += OnSceneGUI;
#endif
			ProGridsInterface.SubscribePushToGridEvent(PushToGrid);
			ProGridsInterface.SubscribeToolbarEvent(ProGridsToolbarOpen);
			MeshSelection.objectSelectionChanged += OnObjectSelectionChanged;

			ProGridsToolbarOpen(ProGridsInterface.SceneToolbarIsExtended());

#if !UNITY_2018_2_OR_NEWER
			s_ResetOnSceneGUIState = typeof(SceneView).GetMethod("ResetOnSceneGUIState", BindingFlags.Instance | BindingFlags.NonPublic);
#endif

			LoadSettings();
			InitGUI();
			UpdateSelection();
			HideSelectedWireframe();

			if (selectModeChanged != null)
				selectModeChanged(selectMode);
		}

		void OnDisable()
		{
			s_Instance = null;

			if (s_EditorToolbar != null)
				DestroyImmediate(s_EditorToolbar);

			ClearElementSelection();

			UpdateSelection();

			m_EditorMeshHandles.Dispose();

			if (selectionUpdated != null)
				selectionUpdated(null);

#if UNITY_2019_1_OR_NEWER
			SceneView.duringSceneGui -= OnSceneGUI;
#else
			SceneView.onSceneGUIDelegate -= OnSceneGUI;
#endif
			ProGridsInterface.UnsubscribePushToGridEvent(PushToGrid);
			ProGridsInterface.UnsubscribeToolbarEvent(ProGridsToolbarOpen);
			MeshSelection.objectSelectionChanged -= OnObjectSelectionChanged;

			// re-enable unity wireframe
			// todo set wireframe override in pb_Selection, no pb_Editor
			foreach (var pb in FindObjectsOfType<ProBuilderMesh>())
				EditorUtility.SetSelectionRenderState(pb.gameObject.GetComponent<Renderer>(),
					EditorUtility.GetSelectionRenderState());

			SceneView.RepaintAll();
		}

		internal static void ReloadSettings()
		{
			if(s_Instance != null)
				s_Instance.LoadSettings();
			SceneView.RepaintAll();
		}

		void LoadSettings()
		{
			m_EditorMeshHandles.ReloadPreferences();

			m_ScenePickerPreferences = new ScenePickerPreferences()
			{
				maxPointerDistance = ScenePickerPreferences.maxPointerDistanceFuzzy,
				cullMode = m_BackfaceSelectEnabled ? CullingMode.None : CullingMode.Back,
				selectionModifierBehavior = m_SelectModifierBehavior,
				rectSelectMode = m_DragSelectRectMode
			};

			// workaround for old single-key shortcuts
			if(s_Shortcuts.value == null || s_Shortcuts.value.Length < 1)
				s_Shortcuts.SetValue(Shortcut.DefaultShortcuts().ToArray(), true);
		}

		void InitGUI()
		{
			if (s_EditorToolbar != null)
				Object.DestroyImmediate(s_EditorToolbar);

			s_EditorToolbar = ScriptableObject.CreateInstance<EditorToolbar>();
			s_EditorToolbar.hideFlags = HideFlags.HideAndDontSave;
			s_EditorToolbar.InitWindowProperties(this);

			VertexTranslationInfoStyle = new GUIStyle();
			VertexTranslationInfoStyle.normal.background = EditorGUIUtility.whiteTexture;
			VertexTranslationInfoStyle.normal.textColor = new Color(1f, 1f, 1f, .6f);
			VertexTranslationInfoStyle.padding = new RectOffset(3, 3, 3, 0);

			var object_Graphic_off = IconUtility.GetIcon("Modes/Mode_Object");
			var face_Graphic_off = IconUtility.GetIcon("Modes/Mode_Face");
			var vertex_Graphic_off = IconUtility.GetIcon("Modes/Mode_Vertex");
			var edge_Graphic_off = IconUtility.GetIcon("Modes/Mode_Edge");

			m_EditModeIcons = new GUIContent[]
			{
				object_Graphic_off != null
					? new GUIContent(object_Graphic_off, "Object Selection")
					: new GUIContent("OBJ", "Object Selection"),
				vertex_Graphic_off != null
					? new GUIContent(vertex_Graphic_off, "Vertex Selection")
					: new GUIContent("VRT", "Vertex Selection"),
				edge_Graphic_off != null
					? new GUIContent(edge_Graphic_off, "Edge Selection")
					: new GUIContent("EDG", "Edge Selection"),
				face_Graphic_off != null
					? new GUIContent(face_Graphic_off, "Face Selection")
					: new GUIContent("FCE", "Face Selection"),
			};
		}

		/// <summary>
		/// Rebuild the mesh wireframe and selection caches.
		/// </summary>
		public static void Refresh(bool vertexCountChanged = true)
		{
			if (instance != null)
				instance.UpdateSelection(vertexCountChanged);
		}

		void OnGUI()
		{
			if (m_CommandStyle == null)
				m_CommandStyle = EditorGUIUtility.GetBuiltinSkin(EditorSkin.Inspector).FindStyle("Command");

			Event e = Event.current;

			switch (e.type)
			{
				case EventType.ContextClick:
					var menu = new GenericMenu();
					AddItemsToMenu(menu);
					menu.ShowAsContext();
					break;

				case EventType.KeyDown:
					if (s_Shortcuts.value.Any(x => x.Matches(e.keyCode, e.modifiers)))
						e.Use();
					break;

				case EventType.KeyUp:
					ShortcutCheck(e);
					break;
			}

			if (s_EditorToolbar != null)
			{
				s_EditorToolbar.OnGUI();
			}
			else
			{
				try
				{
					InitGUI();
				}
				catch (System.Exception exception)
				{
					Debug.LogWarning(string.Format("Failed initializing ProBuilder Toolbar:\n{0}", exception.ToString()));
				}
			}
		}

		void Menu_ToggleIconMode()
		{
			s_IsIconGui.value = !s_IsIconGui;
			if (s_EditorToolbar != null)
				Object.DestroyImmediate(s_EditorToolbar);
			s_EditorToolbar = ScriptableObject.CreateInstance<EditorToolbar>();
			s_EditorToolbar.hideFlags = HideFlags.HideAndDontSave;
			s_EditorToolbar.InitWindowProperties(this);
		}

		public void AddItemsToMenu(GenericMenu menu)
		{
			bool floating = s_WindowIsFloating;

			menu.AddItem(new GUIContent("Window/Open as Floating Window", ""), floating, () => SetIsUtilityWindow(true) );
			menu.AddItem(new GUIContent("Window/Open as Dockable Window", ""), !floating, () => SetIsUtilityWindow(false) );
			menu.AddSeparator("");

			menu.AddItem(new GUIContent("Use Icon Mode", ""), s_IsIconGui,
				Menu_ToggleIconMode);
			menu.AddItem(new GUIContent("Use Text Mode", ""), !s_IsIconGui,
				Menu_ToggleIconMode);
		}

		void SetIsUtilityWindow(bool isUtilityWindow)
		{
			s_WindowIsFloating.value = isUtilityWindow;
			var windowTitle = titleContent;
			Close();
			var res = GetWindow(GetType(), isUtilityWindow);
			res.titleContent = windowTitle;
		}

		void OnSceneGUI(SceneView sceneView)
		{
#if !UNITY_2018_2_OR_NEWER
			if(s_ResetOnSceneGUIState != null)
				s_ResetOnSceneGUIState.Invoke(sceneView, null);
#endif

			SceneStyles.Init();

			m_CurrentEvent = Event.current;

//			if(selectMode.ContainsFlag(SelectMode.Face | SelectMode.Edge | SelectMode.Vertex))
//			{
//				if (m_CurrentEvent.Equals(Event.KeyboardEvent("v")))
//					m_DoSnapToVertex = true;
//				else if (m_CurrentEvent.Equals(Event.KeyboardEvent("c")))
//					m_DoSnapToFace = true;
//			}

			// Snap stuff
//			if (m_CurrentEvent.type == EventType.KeyUp)
//			{
//				m_DoSnapToFace = false;
//				m_DoSnapToVertex = false;
//			}

			if (m_CurrentEvent.type == EventType.MouseDown && m_CurrentEvent.button == 1)
				m_IsRightMouseDown = true;

			if (m_CurrentEvent.type == EventType.MouseUp && m_CurrentEvent.button == 1 || m_CurrentEvent.type == EventType.Ignore)
				m_IsRightMouseDown = false;

			m_EditorMeshHandles.DrawSceneHandles(SceneDragAndDropListener.isDragging ? SelectMode.None : selectMode);

			DrawHandleGUI(sceneView);

			if (!m_IsRightMouseDown && (m_CurrentEvent.type == EventType.KeyUp ? m_CurrentEvent.keyCode : KeyCode.None) != KeyCode.None)
			{
				if (ShortcutCheck(m_CurrentEvent))
				{
					m_CurrentEvent.Use();
					return;
				}
			}

			if (m_CurrentEvent.type == EventType.KeyDown)
			{
				if (s_Shortcuts.value.Any(x => x.Matches(m_CurrentEvent.keyCode, m_CurrentEvent.modifiers)))
					m_CurrentEvent.Use();
			}

			if (selectMode == SelectMode.Object)
				return;
//
//			// Finished moving vertices, scaling, or adjusting uvs
//			if ((m_IsMovingElements || m_IsMovingTextures) && GUIUtility.hotControl < 1)
//			{
//				OnFinishVertexModification();
//				m_HandleRotation = MeshSelection.GetHandleRotation(s_HandleOrientation);
//				UpdateTextureHandles();
//			}

			// Check mouse position in scene and determine if we should highlight something
			if (s_ShowHoverHighlight
				&& m_CurrentEvent.type == EventType.MouseMove
				&& selectMode.ContainsFlag(SelectMode.Face | SelectMode.Edge | SelectMode.Vertex | SelectMode.TextureFace))
			{
				m_Hovering.CopyTo(m_HoveringPrevious);

				if(GUIUtility.hotControl == 0)
					EditorSceneViewPicker.MouseRayHitTest(m_CurrentEvent.mousePosition, selectMode, m_ScenePickerPreferences, m_Hovering);
				else
					m_Hovering.Clear();

				if (!m_Hovering.Equals(m_HoveringPrevious))
					SceneView.RepaintAll();
			}

			if (Tools.current == Tool.View)
				return;

			// Overrides the toolbar transform tools
			if (Tools.current != Tool.None && Tools.current != m_CurrentTool)
				SetTool_Internal(Tools.current);

			Tools.current = Tool.None;

			if (selectMode.ContainsFlag(SelectMode.Vertex | SelectMode.Edge | SelectMode.Face | SelectMode.TextureFace))
			{
				if (MeshSelection.selectedVertexCount > 0)
				{
					if (selectMode == SelectMode.TextureFace)
					{
						switch (m_CurrentTool)
						{
							case Tool.Move:
								TextureMoveTool();
								break;
							case Tool.Rotate:
								TextureRotateTool();
								break;
							case Tool.Scale:
								TextureScaleTool();
								break;
						}
					}
					else
					{
						switch (m_CurrentTool)
						{
							case Tool.Move:
								VertexMoveTool();
								break;
							case Tool.Scale:
								VertexScaleTool();
								break;
							case Tool.Rotate:
								VertexRotateTool();
								break;
						}
					}

				}
			}

			if (EditorHandleUtility.SceneViewInUse(m_CurrentEvent) || m_CurrentEvent.isKey)
			{
				m_IsDragging = false;
				return;
			}

			// This prevents us from selecting other objects in the scene,
			// and allows for the selection of faces / vertices.
			m_DefaultControl = GUIUtility.GetControlID(FocusType.Passive);
			HandleUtility.AddDefaultControl(m_DefaultControl);

			if (m_CurrentEvent.type == EventType.MouseDown)
			{
				// double clicking object
				if (m_CurrentEvent.clickCount > 1)
				{
					DoubleClick(m_CurrentEvent);
				}

				m_InitialMousePosition = m_CurrentEvent.mousePosition;
				// readyForMouseDrag prevents a bug wherein after ending a drag an errant
				// MouseDrag event is sent with no corresponding MouseDown/MouseUp event.
				m_IsReadyForMouseDrag = true;
			}

			if (m_CurrentEvent.type == EventType.MouseDrag && m_IsReadyForMouseDrag)
			{
				if (!m_IsDragging && Vector2.Distance(m_CurrentEvent.mousePosition, m_InitialMousePosition) > k_MouseDragThreshold)
				{
					sceneView.Repaint();
					m_IsDragging = true;
				}
			}

			if (m_CurrentEvent.type == EventType.Ignore)
			{
				if (m_IsDragging)
				{
					m_IsReadyForMouseDrag = false;
					m_IsDragging = false;
					EditorSceneViewPicker.DoMouseDrag(m_MouseDragRect, selectMode, m_ScenePickerPreferences);
				}

				if (m_WasDoubleClick)
					m_WasDoubleClick = false;
			}

			if (m_CurrentEvent.type == EventType.MouseUp)
			{
				if (m_WasDoubleClick)
				{
					m_WasDoubleClick = false;
				}
				else
				{
					if (!m_IsDragging)
					{
						if (UVEditor.instance)
							UVEditor.instance.ResetUserPivot();

						EditorSceneViewPicker.DoMouseClick(m_CurrentEvent, selectMode, m_ScenePickerPreferences);
						UpdateSelection();
						SceneView.RepaintAll();
					}
					else
					{
						m_IsDragging = false;
						m_IsReadyForMouseDrag = false;

						if (UVEditor.instance)
							UVEditor.instance.ResetUserPivot();

						EditorSceneViewPicker.DoMouseDrag(m_MouseDragRect, selectMode, m_ScenePickerPreferences);
					}
				}
			}
		}

		void DoubleClick(Event e)
		{
			var mesh = EditorSceneViewPicker.DoMouseClick(m_CurrentEvent, selectMode, m_ScenePickerPreferences);

			if (mesh != null)
			{
				if (selectMode == SelectMode.Edge)
				{
					if (e.shift)
						EditorUtility.ShowNotification(EditorToolbarLoader.GetInstance<Actions.SelectEdgeRing>().DoAction());
					else
						EditorUtility.ShowNotification(EditorToolbarLoader.GetInstance<Actions.SelectEdgeLoop>().DoAction());
				}
				else if (selectMode == SelectMode.Face)
				{
					if ((e.modifiers & (EventModifiers.Control | EventModifiers.Shift)) ==
						(EventModifiers.Control | EventModifiers.Shift))
						Actions.SelectFaceRing.MenuRingAndLoopFaces(selection);
					else if (e.control)
						EditorUtility.ShowNotification(EditorToolbarLoader.GetInstance<Actions.SelectFaceRing>().DoAction());
					else if (e.shift)
						EditorUtility.ShowNotification(EditorToolbarLoader.GetInstance<Actions.SelectFaceLoop>().DoAction());
					else
						mesh.SetSelectedFaces(mesh.facesInternal);
				}
				else
				{
					mesh.SetSelectedFaces(mesh.facesInternal);
				}

				UpdateSelection();
				SceneView.RepaintAll();
				m_WasDoubleClick = true;
			}
		}

		static VertexManipulationTool s_MoveTool;
		static VertexManipulationTool s_RotateTool;
		static VertexManipulationTool m_ScaleTool;
#pragma warning disable 612
		void VertexMoveTool()
		{
			if(s_MoveTool == null)
				s_MoveTool = new MoveTool();

			s_MoveTool.OnSceneGUI(Event.current);

			return;
//
//			if (!m_IsMovingElements)
//				m_ElementHandlePosition = m_HandlePosition;
//
//			m_ElementHandleCachedPosition = m_ElementHandlePosition;
//
//			m_ElementHandlePosition = Handles.PositionHandle(m_ElementHandlePosition, m_HandleRotation);
//
//			if (m_CurrentEvent.alt)
//				return;
//
//			if (m_ElementHandlePosition != m_ElementHandleCachedPosition)
//			{
//				Vector3 diff = m_ElementHandlePosition - m_ElementHandleCachedPosition;
//
//				Vector3 mask = diff.ToMask(Math.handleEpsilon);
//
//				if (m_DoSnapToVertex)
//				{
//					Vector3 v;
//
//					if (FindNearestVertex(m_CurrentEvent.mousePosition, out v))
//						diff = Vector3.Scale(v - m_ElementHandleCachedPosition, mask);
//				}
//				else if (m_DoSnapToFace)
//				{
//					ProBuilderMesh obj = null;
//					RaycastHit hit;
//					Dictionary<ProBuilderMesh, HashSet<Face>> ignore = new Dictionary<ProBuilderMesh, HashSet<Face>>();
//					foreach (ProBuilderMesh pb in selection)
//						ignore.Add(pb, new HashSet<Face>(pb.selectedFacesInternal));
//
//					if (EditorHandleUtility.FaceRaycast(m_CurrentEvent.mousePosition, out obj, out hit, ignore))
//					{
//						if (mask.IntSum() == 1)
//						{
//							Ray r = new Ray(m_ElementHandleCachedPosition, -mask);
//							Plane plane = new Plane(obj.transform.TransformDirection(hit.normal).normalized,
//								obj.transform.TransformPoint(hit.point));
//
//							float forward, backward;
//							plane.Raycast(r, out forward);
//							plane.Raycast(r, out backward);
//							float planeHit = Mathf.Abs(forward) < Mathf.Abs(backward) ? forward : backward;
//							r.direction = -r.direction;
//							plane.Raycast(r, out forward);
//							plane.Raycast(r, out backward);
//							float rev = Mathf.Abs(forward) < Mathf.Abs(backward) ? forward : backward;
//							if (Mathf.Abs(rev) > Mathf.Abs(planeHit))
//								planeHit = rev;
//
//							if (Mathf.Abs(planeHit) > Mathf.Epsilon)
//								diff = mask * -planeHit;
//						}
//						else
//						{
//							diff = Vector3.Scale(obj.transform.TransformPoint(hit.point) - m_ElementHandleCachedPosition, mask.Abs());
//						}
//					}
//				}
//
//				if (!m_IsMovingElements)
//				{
//					m_IsMovingElements = true;
//					m_TranslateOrigin = m_ElementHandleCachedPosition;
//					m_RotateOrigin = m_HandleRotation.eulerAngles;
//					m_ScaleOrigin = m_HandleScale;
//
//					OnBeginVertexMovement();
//
//					ProGridsInterface.OnHandleMove(mask);
//				}
//
//				for (int i = 0; i < selection.Length; i++)
//				{
//					var mesh = selection[i];
//
//					mesh.TranslateVerticesInWorldSpace(mesh.selectedIndexesInternal,
//						diff,
//						m_SnapEnabled ? m_SnapValue : 0f,
//						m_SnapAxisConstraint);
//
//					mesh.RefreshUV(MeshSelection.selectedFacesInEditZone[mesh]);
//					mesh.Refresh(RefreshMask.Normals);
//					mesh.mesh.RecalculateBounds();
//				}
//
//				UpdateMeshHandles(false);
//			}
		}

		void VertexScaleTool()
		{
			if (m_ScaleTool == null)
				m_ScaleTool = new ScaleTool();

			m_ScaleTool.OnSceneGUI(Event.current);
		}

		void VertexRotateTool()
		{
			if (s_RotateTool == null)
				s_RotateTool = new RotateTool();

			s_RotateTool.OnSceneGUI(Event.current);
		}

#pragma warning restore 612

		void TextureMoveTool()
		{
//			UVEditor uvEditor = UVEditor.instance;
//			if (!uvEditor) return;
//
//			Vector3 cached = m_TextureHandlePosition;
//
//			m_TextureHandlePosition = Handles.PositionHandle(m_TextureHandlePosition, m_HandleRotation);
//
//			if (m_CurrentEvent.alt) return;
//
//			if (m_TextureHandlePosition != cached)
//			{
//				cached = Quaternion.Inverse(m_HandleRotation) * m_TextureHandlePosition;
//				cached.y = -cached.y;
//
//				Vector3 lossyScale = selection[0].transform.lossyScale;
//				Vector3 pos = cached.DivideBy(lossyScale);
//
//				if (!m_IsMovingTextures)
//				{
//					m_TextureHandlePositionPrevious = pos;
//					m_IsMovingTextures = true;
//				}
//
//				uvEditor.SceneMoveTool(pos - m_TextureHandlePositionPrevious);
//				m_TextureHandlePositionPrevious = pos;
//				uvEditor.Repaint();
//			}
		}

		void TextureRotateTool()
		{
//			UVEditor uvEditor = UVEditor.instance;
//			if (!uvEditor) return;
//
//			float size = HandleUtility.GetHandleSize(m_HandlePosition);
//
//			if (m_CurrentEvent.alt) return;
//
//			Matrix4x4 prev = Handles.matrix;
//			Handles.matrix = handleMatrix;
//
//			Quaternion cached = m_TextureRotation;
//
//			m_TextureRotation = Handles.Disc(m_TextureRotation, Vector3.zero, Vector3.forward, size, false, 0f);
//
//			if (m_TextureRotation != cached)
//			{
//				if (!m_IsMovingTextures)
//					m_IsMovingTextures = true;
//
//				uvEditor.SceneRotateTool(-m_TextureRotation.eulerAngles.z);
//			}
//
//			Handles.matrix = prev;
		}

		void TextureScaleTool()
		{
//			UVEditor uvEditor = UVEditor.instance;
//			if (!uvEditor) return;
//
//			float size = HandleUtility.GetHandleSize(m_HandlePosition);
//
//			Matrix4x4 prev = Handles.matrix;
//			Handles.matrix = handleMatrix;
//
//			Vector3 cached = m_TextureScale;
//			m_TextureScale = Handles.ScaleHandle(m_TextureScale, Vector3.zero, Quaternion.identity, size);
//
//			if (m_CurrentEvent.alt) return;
//
//			if (cached != m_TextureScale)
//			{
//				if (!m_IsMovingTextures)
//					m_IsMovingTextures = true;
//
//				uvEditor.SceneScaleTool(m_TextureScale, cached);
//			}
//
//			Handles.matrix = prev;
		}

		void DrawHandleGUI(SceneView sceneView)
		{
			if (sceneView != SceneView.lastActiveSceneView)
				return;

			if (m_CurrentEvent.type == EventType.Repaint
				&& !SceneDragAndDropListener.isDragging
				&& m_Hovering != null
				&& GUIUtility.hotControl == 0
				&& HandleUtility.nearestControl == m_DefaultControl
				&& selectMode.IsMeshElementMode())
			{
				try
				{
					m_EditorMeshHandles.DrawSceneSelection(m_Hovering);
				}
				catch
				{
					; // this happens on undo, when c++ object is destroyed but c# side thinks it's still alive
				}
			}

			using (new HandleGUI())
			{
				int screenWidth = (int) sceneView.position.width;
				int screenHeight = (int) sceneView.position.height;

				int currentSelectionMode = -1;

				switch (m_SelectMode.value)
				{
					case SelectMode.Object:
						currentSelectionMode = 0;
						break;
					case SelectMode.Vertex:
						currentSelectionMode = 1;
						break;
					case SelectMode.Edge:
						currentSelectionMode = 2;
						break;
					case SelectMode.Face:
						currentSelectionMode = 3;
						break;
					default:
						currentSelectionMode = -1;
						break;
				}

				switch ((SceneToolbarLocation) s_SceneToolbarLocation)
				{
					case SceneToolbarLocation.BottomCenter:
						m_ElementModeToolbarRect.x = (screenWidth / 2 - 64);
						m_ElementModeToolbarRect.y = screenHeight - m_ElementModeToolbarRect.height * 3;
						break;

					case SceneToolbarLocation.BottomLeft:
						m_ElementModeToolbarRect.x = 12;
						m_ElementModeToolbarRect.y = screenHeight - m_ElementModeToolbarRect.height * 3;
						break;

					case SceneToolbarLocation.BottomRight:
						m_ElementModeToolbarRect.x = screenWidth - (m_ElementModeToolbarRect.width + 12);
						m_ElementModeToolbarRect.y = screenHeight - m_ElementModeToolbarRect.height * 3;
						break;

					case SceneToolbarLocation.UpperLeft:
						m_ElementModeToolbarRect.x = 12;
						m_ElementModeToolbarRect.y = 10;
						break;

					case SceneToolbarLocation.UpperRight:
						m_ElementModeToolbarRect.x = screenWidth - (m_ElementModeToolbarRect.width + 96);
						m_ElementModeToolbarRect.y = 10;
						break;

					default:
					case SceneToolbarLocation.UpperCenter:
						m_ElementModeToolbarRect.x = (screenWidth / 2 - 64);
						m_ElementModeToolbarRect.y = 10;
						break;
				}

				EditorGUI.BeginChangeCheck();

				currentSelectionMode =
					GUI.Toolbar(m_ElementModeToolbarRect, (int) currentSelectionMode, m_EditModeIcons, m_CommandStyle);

				if (EditorGUI.EndChangeCheck())
				{
					if (currentSelectionMode == 0)
						selectMode = SelectMode.Object;
					else if (currentSelectionMode == 1)
						selectMode = SelectMode.Vertex;
					else if (currentSelectionMode == 2)
						selectMode = SelectMode.Edge;
					else if (currentSelectionMode == 3)
						selectMode = SelectMode.Face;
				}

				// todo Move to VertexManipulationTool
//				if (m_IsMovingElements && s_ShowSceneInfo)
//				{
//					string handleTransformInfo = string.Format(
//						"translate: <b>{0}</b>\nrotate: <b>{1}</b>\nscale: <b>{2}</b>",
//						(m_ElementHandlePosition - m_TranslateOrigin).ToString(),
//						(m_HandleRotation.eulerAngles - m_RotateOrigin).ToString(),
//						(m_HandleScale - m_ScaleOrigin).ToString());
//
//					var gc = UI.EditorGUIUtility.TempContent(handleTransformInfo);
//					// sceneview screen.height includes the tab and toolbar
//					var toolbarHeight = EditorStyles.toolbar.CalcHeight(gc, Screen.width);
//					var size = UI.EditorStyles.sceneTextBox.CalcSize(gc);
//
//					Rect handleTransformInfoRect = new Rect(
//						sceneView.position.width - (size.x + 8), sceneView.position.height - (size.y + 8 + toolbarHeight),
//						size.x,
//						size.y);
//
//					GUI.Label(handleTransformInfoRect, gc, UI.EditorStyles.sceneTextBox);
//				}

				if (s_ShowSceneInfo)
				{
					Vector2 size = UI.EditorStyles.sceneTextBox.CalcSize(m_SceneInfo);
					m_SceneInfoRect.width = size.x;
					m_SceneInfoRect.height = size.y;
					GUI.Label(m_SceneInfoRect, m_SceneInfo, UI.EditorStyles.sceneTextBox);
				}

				if (m_IsDragging)
				{
					if (m_CurrentEvent.type == EventType.Repaint)
					{
						// Always draw from lowest to largest values
						var start = Vector2.Min(m_InitialMousePosition, m_CurrentEvent.mousePosition);
						var end = Vector2.Max(m_InitialMousePosition, m_CurrentEvent.mousePosition);

						m_MouseDragRect = new Rect(start.x, start.y, end.x - start.x, end.y - start.y);

						SceneStyles.selectionRect.Draw(m_MouseDragRect, false, false, false, false);
					}
					else if (m_CurrentEvent.isMouse)
					{
						HandleUtility.Repaint();
					}
				}
			}
		}

		internal bool ShortcutCheck(Event e)
		{
			List<Shortcut> matches = s_Shortcuts.value.Where(x => x.Matches(e.keyCode, e.modifiers)).ToList();

			if (matches.Count < 1)
				return false;

			bool used = false;
			Shortcut usedShortcut = null;

			foreach (Shortcut cut in matches)
			{
				if (AllLevelShortcuts(cut))
				{
					used = true;
					usedShortcut = cut;
					break;
				}
			}

			if (!used)
			{
				foreach (Shortcut cut in matches)
					used |= GeoLevelShortcuts(cut);
			}

			if (used)
				Event.current.Use();

			if(usedShortcut != null)
				EditorUtility.ShowNotification(usedShortcut.action);

			return used;
		}

		bool AllLevelShortcuts(Shortcut shortcut)
		{
			switch (shortcut.action)
			{
				// TODO Remove once a workaround for non-upper-case shortcut chars is found
				case "Toggle Geometry Mode":

					if (selectMode == SelectMode.Object)
						selectMode = m_LastComponentMode;
					else
						selectMode = SelectMode.Object;
					EditorUtility.ShowNotification(selectMode.ToString() + " Editing");
					return true;

				case "Vertex Mode":
				{
					if (!s_UniqueModeShortcuts)
						return false;
					selectMode = SelectMode.Vertex;
					return true;
				}

				case "Edge Mode":
				{
					if (!s_UniqueModeShortcuts)
						return false;
					selectMode = SelectMode.Edge;
					return true;
				}

				case "Face Mode":
				{
					if (!s_UniqueModeShortcuts)
						return false;
					selectMode = SelectMode.Face;
					return true;
				}

				default:
					return false;
			}
		}

		bool GeoLevelShortcuts(Shortcut shortcut)
		{
			switch (shortcut.action)
			{
				case "Escape":
					ClearElementSelection();
					EditorUtility.ShowNotification("Top Level");
					UpdateSelection();
					selectMode = SelectMode.Object;
					return true;

				// TODO Remove once a workaround for non-upper-case shortcut chars is found
				case "Toggle Selection Mode":
					if(s_UniqueModeShortcuts)
						return false;
					ToggleSelectionMode();
					EditorUtility.ShowNotification(selectMode.ToString());
					return true;

				case "Delete Face":
					EditorUtility.ShowNotification(EditorToolbarLoader.GetInstance<DeleteFaces>().DoAction().notification);
					return true;

				/* handle alignment */
				case "Toggle Handle Pivot":
					if (MeshSelection.selectedVertexCount < 1 || selectMode == SelectMode.TextureFace)
						return false;

					ToggleHandleAlignment();
					EditorUtility.ShowNotification("Handle Alignment: " + s_HandleOrientation.value.ToString());
					return true;

				case "Set Pivot":

					if (selection.Length > 0)
					{
						foreach (ProBuilderMesh pbo in selection)
						{
							UndoUtility.RecordObjects(new Object[2] { pbo, pbo.transform }, "Set Pivot");

							if (pbo.selectedIndexesInternal.Length > 0)
							{
								pbo.CenterPivot(pbo.selectedIndexesInternal);
							}
							else
							{
								pbo.CenterPivot(null);
							}
						}

						EditorUtility.ShowNotification("Set Pivot");
					}

					return true;

				default:
					return false;
			}
		}

		/// <summary>
		/// Allows another window to tell the Editor what Tool is now in use. Does *not* update any other windows.
		/// </summary>
		/// <param name="newTool"></param>
		internal void SetTool(Tool newTool)
		{
			m_CurrentTool = newTool;
		}

		/// <summary>
		/// Calls SetTool(), then Updates the UV Editor window if applicable.
		/// </summary>
		/// <param name="newTool"></param>
		void SetTool_Internal(Tool newTool)
		{
			SetTool(newTool);

			if (UVEditor.instance != null)
				UVEditor.instance.SetTool(newTool);
		}

		internal void ToggleHandleAlignment()
		{
			int newHa = (int) s_HandleOrientation.value + 1;
			if (newHa >= Enum.GetValues(typeof(HandleOrientation)).Length)
				newHa = 0;
			handleOrientation = ((HandleOrientation) newHa);
		}

		/// <summary>
		/// Toggles between the SelectMode values and updates the graphic handles as necessary.
		/// </summary>
		internal void ToggleSelectionMode()
		{
			if (m_SelectMode == SelectMode.Vertex)
				m_SelectMode.SetValue(SelectMode.Edge, true);
			else if (m_SelectMode == SelectMode.Edge)
				m_SelectMode.SetValue(SelectMode.Face, true);
			else if (m_SelectMode == SelectMode.Face)
				m_SelectMode.SetValue(SelectMode.Vertex, true);
		}

		void UpdateSelection(bool selectionChanged = true)
		{
			// todo remove selection property
			selection = MeshSelection.topInternal.ToArray();

//			m_HandlePosition = MeshSelection.GetHandlePosition();
//			m_HandleRotation = MeshSelection.GetHandleRotation(s_HandleOrientation);

			UpdateTextureHandles();
			UpdateMeshHandles(selectionChanged);

			if (selectionChanged)
			{
				UpdateSceneInfo();
				MeshSelection.OnComponentSelectionChanged();
			}

			if (selectionUpdated != null)
				selectionUpdated(selection);
		}

		internal static void UpdateMeshHandles(bool selectionOrVertexCountChanged = true)
		{
			if (!s_Instance)
				return;

			if (s_Instance.m_EditorMeshHandles == null)
				return;

			try
			{
				s_Instance.m_EditorMeshHandles.RebuildSelectedHandles(MeshSelection.topInternal, selectMode, selectionOrVertexCountChanged);
			}
			catch
			{
				// happens on undo when c++ object is gone but c# isn't in the know
				s_Instance.m_EditorMeshHandles.ClearHandles();
			}
		}

		void UpdateSceneInfo()
		{
			m_SceneInfo.text = string.Format(
				"Faces: <b>{0}</b>\nTriangles: <b>{1}</b>\nVertices: <b>{2} ({3})</b>\n\nSelected Faces: <b>{4}</b>\nSelected Edges: <b>{5}</b>\nSelected Vertices: <b>{6} ({7})</b>",
				MeshSelection.totalFaceCount,
				MeshSelection.totalTriangleCountCompiled,
				MeshSelection.totalCommonVertexCount,
				MeshSelection.totalVertexCountOptimized,
				MeshSelection.selectedFaceCount,
				MeshSelection.selectedEdgeCount,
				MeshSelection.selectedSharedVertexCount,
				MeshSelection.selectedVertexCount);
		}

		internal void ClearElementSelection()
		{
			foreach (ProBuilderMesh pb in selection)
				pb.ClearSelection();

			m_Hovering.Clear();
		}

		void UpdateTextureHandles()
		{
//			if (!selectMode.ContainsFlag(SelectMode.TextureFace) || !selection.Any())
//				return;
//
//			// Reset temp vars
//			m_TextureHandlePosition = m_HandlePosition;
//			m_TextureScale = Vector3.one;
//			m_TextureRotation = Quaternion.identity;
//
//			ProBuilderMesh pb;
//			Face face;
//
//			handleMatrix = selection[0].transform.localToWorldMatrix;
//
//			if (GetFirstSelectedFace(out pb, out face))
//			{
//				var normals = Math.NormalTangentBitangent(pb, face);
//				var nrm = normals.normal;
//				var bitan = normals.bitangent;
//
//				if (nrm == Vector3.zero || bitan == Vector3.zero)
//				{
//					nrm = Vector3.up;
//					bitan = Vector3.right;
//				}
//
//				handleMatrix *= Matrix4x4.TRS(Math.GetBounds(pb.positionsInternal.ValuesWithIndexes(face.distinctIndexesInternal)).center,
//					Quaternion.LookRotation(nrm, bitan), Vector3.one);
//			}
		}

		/// <summary>
		/// If dragging a texture aroudn, this method ensures that if it's a member of a texture group it's cronies are also selected
		/// </summary>
		void VerifyTextureGroupSelection()
		{
			bool selectionModified = false;

			foreach (ProBuilderMesh mesh in selection)
			{
				List<int> alreadyChecked = new List<int>();

				foreach (Face f in mesh.selectedFacesInternal)
				{
					int tg = f.textureGroup;

					if (tg > 0 && !alreadyChecked.Contains(f.textureGroup))
					{
						foreach (Face j in mesh.facesInternal)
						{
							if (j != f && j.textureGroup == tg && !mesh.selectedFacesInternal.Contains(j))
							{
								List<Face> newFaceSection = new List<Face>();
								foreach (Face jf in mesh.facesInternal)
									if (jf.textureGroup == tg)
										newFaceSection.Add(jf);
								mesh.SetSelectedFaces(newFaceSection.ToArray());
								selectionModified = true;
								break;
							}
						}
					}

					alreadyChecked.Add(f.textureGroup);
				}
			}

			if(selectionModified)
				UpdateSelection(true);
		}

		void OnObjectSelectionChanged()
		{
			m_Hovering.Clear();
			UpdateSelection();
			HideSelectedWireframe();
		}

		/// <summary>
		/// Hide the default unity wireframe renderer
		/// </summary>
		void HideSelectedWireframe()
		{
			foreach (ProBuilderMesh pb in selection)
				EditorUtility.SetSelectionRenderState(pb.gameObject.GetComponent<Renderer>(),
					EditorUtility.GetSelectionRenderState() & SelectionRenderState.Outline);

			SceneView.RepaintAll();
		}

		/// <summary>
		/// Called from ProGrids.
		/// </summary>
		/// <param name="snapVal"></param>
		void PushToGrid(float snapVal)
		{
			UndoUtility.RecordSelection(selection, "Push elements to Grid");

			if (selectMode == SelectMode.Object || selectMode == SelectMode.None)
				return;

			for (int i = 0; i < selection.Length; i++)
			{
				ProBuilderMesh mesh = selection[i];
				if (mesh.selectedVertexCount < 1)
					continue;

				var indexes = mesh.GetCoincidentVertices(mesh.selectedIndexesInternal);
				Snapping.SnapVertices(mesh, indexes, Vector3.one * snapVal);

				mesh.ToMesh();
				mesh.Refresh();
				mesh.Optimize();
			}

			UpdateSelection();
		}

		void ProGridsToolbarOpen(bool menuOpen)
		{
			bool active = ProGridsInterface.ProGridsActive();
			m_SceneInfoRect.y = active && !menuOpen ? 28 : 10;
			m_SceneInfoRect.x = active ? (menuOpen ? 64 : 8) : 10;
		}

		/// <summary>
		/// A tool, any tool, has just been engaged while in texture mode
		/// </summary>
		internal void OnBeginTextureModification()
		{
			VerifyTextureGroupSelection();
		}

		/// <summary>
		/// Returns the first selected pb_Object and pb_Face, or false if not found.
		/// </summary>
		/// <param name="pb"></param>
		/// <param name="face"></param>
		/// <returns></returns>
		internal bool GetFirstSelectedFace(out ProBuilderMesh pb, out Face face)
		{
			pb = null;
			face = null;

			if (selection.Length < 1) return false;

			pb = selection.FirstOrDefault(x => x.selectedFaceCount > 0);

			if (pb == null)
				return false;

			face = pb.selectedFacesInternal[0];

			return true;
		}

		/// <summary>
		/// Returns the first selected pb_Object and pb_Face, or false if not found.
		/// </summary>
		/// <param name="mat"></param>
		/// <returns></returns>
		internal Material GetFirstSelectedMaterial()
		{
			for (int i = 0; i < selection.Length; i++)
			{
				var mesh = selection[i];

				for (int n = 0; n < mesh.selectedFaceCount; n++)
				{
					var face = mesh.selectedFacesInternal[i];
					var mat = UnityEngine.ProBuilder.MeshUtility.GetSharedMaterial(selection[i].renderer, face.submeshIndex);
					if (mat != null)
						return mat;
				}
			}

			return null;
		}
	}
}
