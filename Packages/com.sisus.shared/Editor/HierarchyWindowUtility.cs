using System;
using UnityEditor;

namespace Sisus.Shared.EditorOnly
{
	public static class HierarchyWindowUtility
	{
		public static EditorWindow LastInteractedHierarchyWindow => SceneHierarchyWindow.lastInteractedHierarchyWindow;
		
		public static bool IsExpanded(int itemId)
		{
			foreach(var sceneHierarchyWindow in SceneHierarchyWindow.GetAllSceneHierarchyWindows())
			{
				if(Array.IndexOf(sceneHierarchyWindow.GetExpandedIDs(), itemId) != -1)
				{
					return true;
				}
			}

			return false;
		}
		
		public static int GetItemBeingRenamedId()
		{
			var window = SceneHierarchyWindow.lastInteractedHierarchyWindow;
			if(!window)
			{
				return -1;
			}
			
			var renameOverlay = window.sceneHierarchy.treeView.state.renameOverlay;
			if(!renameOverlay.IsRenaming())
			{
				return -1;
			}

			return renameOverlay.userData;
		}

		public static int GetDraggedItemId()
		{
			var window = SceneHierarchyWindow.lastInteractedHierarchyWindow;
			if(!window)
			{
				return -1;
			}
		
			var treeView = window.sceneHierarchy.treeView;
			if(!treeView.isDragging)
			{
				return -1;
			}
			
			return treeView.dragging?.GetDropTargetControlID() ?? -1;
		}

		public static bool IsDraggedOrSelected(int instanceId)
		{
			var window = SceneHierarchyWindow.lastInteractedHierarchyWindow;
			if(!window)
			{
				return false;
			}

			var treeView = window.sceneHierarchy.treeView;
			if(treeView.isDragging)
			{
				return treeView.IsItemDragSelectedOrSelected(new(instanceId, 0));
			}

			return treeView.HasSelection() && treeView.IsSelected(instanceId);
		}

		public static void SetExpandedRecursive(int instanceId, bool expand) => SceneHierarchyWindow.lastInteractedHierarchyWindow.SetExpandedRecursive(instanceId, expand);

		public static bool IsHierarchyWindowFocused()
		{
			var window = SceneHierarchyWindow.lastInteractedHierarchyWindow;
			return window && window.sceneHierarchy.treeView.HasFocus();
		}
	}
}