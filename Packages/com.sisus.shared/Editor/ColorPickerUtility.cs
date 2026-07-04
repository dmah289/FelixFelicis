using UnityEditor;
using UnityEngine;

namespace Sisus.Shared
{
	public static class ColorGUIUtility
	{
		public static Color DrawField(Rect position, Color value, bool showEyedropper, bool showAlpha) => EditorGUI.ColorField(position, value, showEyedropper, showAlpha);
	}
}
