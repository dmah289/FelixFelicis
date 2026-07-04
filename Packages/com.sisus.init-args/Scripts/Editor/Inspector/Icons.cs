using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Sisus.Init.EditorOnly.Internal
{
	[FilePath("Init(args)/Icons.asset", FilePathAttribute.Location.PreferencesFolder)]
	public class Icons : ScriptableSingleton<Icons>
	{
		[SerializeField]
		private Texture2D nullGuardPassedIcon;

		public static Texture2D NullGuardPassedIcon => instance.nullGuardPassedIcon;

		private void OnEnable()
		{
			var stateChanged = UpdateNullGuardPassedIcon();
			if(stateChanged)
			{
				Save(true);
			}
		}

		private bool UpdateNullGuardPassedIcon()
		{
			var nullGuardPassedIconName = "NullGuardPassed@2x";
			if(EditorGUIUtility.isProSkin)
			{
				nullGuardPassedIconName = "d_" + nullGuardPassedIconName;
			}

			if (nullGuardPassedIcon && nullGuardPassedIcon.name == nullGuardPassedIconName)
			{
				return false;
			}

			var expectedAssetPath =  "Packages/com.sisus.init-args/Icons/" + nullGuardPassedIconName;
			nullGuardPassedIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(expectedAssetPath);
			if (nullGuardPassedIcon)
			{
				return true;
			}

			var guid = AssetDatabase.FindAssets("t:Texture2D " + nullGuardPassedIconName).FirstOrDefault();
			if(guid is null)
			{
				const string FallbackIconName = "TestPassed";
				nullGuardPassedIcon = (Texture2D)EditorGUIUtility.IconContent(FallbackIconName).image;

				#if DEV_MODE
				Debug.LogWarning($"Icon '{nullGuardPassedIconName}' not found anywhere in the project. Using fallback '{FallbackIconName}'.");
				Debug.Assert(nullGuardPassedIcon);
				#endif

				return true;
			}

			var foundAtPath = AssetDatabase.GUIDToAssetPath(guid);
			nullGuardPassedIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(foundAtPath);

			#if DEV_MODE
			Debug.LogWarning($"Icon not found at expected path '{expectedAssetPath}', but was found at '{foundAtPath}'.");
			Debug.Assert(nullGuardPassedIcon);
			#endif

			return true;

		}
	}
}