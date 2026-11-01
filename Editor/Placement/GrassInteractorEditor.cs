using UnityEditor;

namespace Cone.Grass.Editor
{
    [CustomEditor(typeof(GrassInteractor)), CanEditMultipleObjects]
    internal sealed class GrassInteractorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            // The facade's public settings configure these base fields before each
            // sample. Keep only the inherited controls that callers can still edit.
            DrawPropertiesExcluding(serializedObject, "m_Script", "actorCollider", "captureShader",
                "acceptedSupportLayers", "footprintPadding", "groundClearance", "attenuateWithClearance",
                "bendStrength", "attackDuration", "recoveryDuration");
            serializedObject.ApplyModifiedProperties();
        }
    }
}
