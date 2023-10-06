﻿using UnityEditor.EditorTools;
using UnityEngine.ProBuilder;
using UnityEngine;
#if UNITY_2020_2_OR_NEWER
using EditorToolManager = UnityEditor.EditorTools.EditorToolManager;
using ToolManager = UnityEditor.EditorTools.ToolManager;
#else
using EditorToolManager = UnityEditor.EditorTools.EditorToolContext;
using ToolManager = UnityEditor.EditorTools.EditorTools;
#endif

namespace UnityEditor.ProBuilder.Actions
{
    sealed class NewShapeToggle : MenuToolToggle
    {
        public override ToolbarGroup group { get { return ToolbarGroup.Tool; } }
        public override Texture2D icon { get { return IconUtility.GetIcon("Toolbar/AddShape"); } }
        public override TooltipContent tooltip { get { return s_Tooltip; } }
        public override string menuTitle { get { return "New Shape"; } }
        public override int toolbarPriority { get { return 0; } }

        static readonly TooltipContent s_Tooltip = new TooltipContent
            (
                "New Shape",
                "Opens the Shape window.\n\nThis tool allows you to interactively create new 3d primitives.",
                keyCommandSuper, keyCommandShift, 'K'
            );

        public override bool enabled
        {
            get { return ProBuilderEditor.instance != null; }
        }

        bool m_ActiveToolIsChanging = false;

        protected override ActionResult PerformActionImplementation()
        {
            m_Tool = (EditorToolManager.GetSingleton<DrawShapeTool>());
            ToolManager.SetActiveTool(m_Tool);

            MenuAction.onPerformAction += ActionPerformed;
            ToolManager.activeToolChanging += OnActiveToolChanged;;

            m_ActiveToolIsChanging = false;

            return new ActionResult(ActionResult.Status.Success,"Draw Shape Tool Starts");
        }

        internal override ActionResult EndActivation()
        {
            MenuAction.onPerformAction -= ActionPerformed;
            ToolManager.activeToolChanging -= OnActiveToolChanged;

            if(!m_ActiveToolIsChanging)
                ToolManager.RestorePreviousPersistentTool();

            ProBuilderEditor.Refresh();

            SceneView.RepaintAll();
            return new ActionResult(ActionResult.Status.Success,"Draw Shape Tool Ends");
        }

        void ActionPerformed(MenuAction newActionPerformed)
        {
            if(ToolManager.IsActiveTool(m_Tool) && newActionPerformed.GetType() != this.GetType())
                LeaveTool();
        }

        void OnActiveToolChanged()
        {
            m_ActiveToolIsChanging = true;
            LeaveTool();
        }

        void LeaveTool()
        {
            ActionResult result = EndActivation();
            EditorUtility.ShowNotification(result.notification);
        }
    }
}
