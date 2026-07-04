//#define DEBUG_REPAINT
//#define DEBUG_ENABLED

#if HIERARCHY_FOLDERS
using UnityEditor;
using UnityEngine;
using static Sisus.Shared.EditorOnly.InspectorContents;

namespace Sisus.Shared.EditorOnly
{
	[InitializeOnLoad]
	internal static class GameObjectHeaderWrapperToInspectorInjector
	{
		static GameObjectHeaderWrapperToInspectorInjector()
		{
			Editor.finishedDefaultHeaderGUI -= AfterInspectorRootEditorHeaderGUI;
			Editor.finishedDefaultHeaderGUI += AfterInspectorRootEditorHeaderGUI;
		}

		private static void AfterInspectorRootEditorHeaderGUI(Editor editor)
		{
			if(!TryGetGameObjectHeaderElement(editor, out var gameObjectHeader)
			   || GameObjectHeaderWrapper.IsWrapped(gameObjectHeader))
			{
				return;
			}
			
			#if DEV_MODE && DEBUG_REPAINT
			Debug.Log(editor.GetType().Name + ".Repaint");
			UnityEngine.Profiling.Profiler.BeginSample("Sisus.Repaint");
			#endif

			editor.Repaint();

			#if DEV_MODE && DEBUG_REPAINT
			UnityEngine.Profiling.Profiler.EndSample();
			#endif

			if(Event.current.type != EventType.Repaint)
			{
				return;
			}

			#if DEV_MODE && DEBUG_ENABLED
			Debug.Log("WRAPPING: " + (editor.target?.GetType().Name ?? "null") + " " + editor.GetType().Name + " ("+editor.GetInstanceID()+") " + gameObjectHeader.name + "." + gameObjectHeader.onGUIHandler.Method.Name); // TEMP
			#endif

			GameObjectHeaderWrapper.Wrap(editor, gameObjectHeader, false);
		}
	}
}
#endif