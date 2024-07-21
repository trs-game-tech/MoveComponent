#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MoveComponentWindow : EditorWindow
{
    private const string MenuName = "CONTEXT/Component/Move Component...";
    private const string SourceComponentDisplayName = "Source Component";
    private const string DestinationGameObjectDisplayName = "Destination GameObject";
    private const string ReplaceReferencesDisplayName = "Replace References";
    private const string UndoName = "Move Component";

    [SerializeField]
    private Component _sourceComponent;

    [SerializeField]
    private GameObject _destinationGameObject;

    [SerializeField]
    private bool _replaceReferences = true;

    private List<Type> _requireComponentOwners;

    [MenuItem(MenuName)]
    private static void Open(MenuCommand menuCommand)
    {
        var window = GetWindow<MoveComponentWindow>();
        var component = menuCommand.context as Component;
        window._sourceComponent = component;
        window._requireComponentOwners = GetRequireComponentOwners(component);
    }

    [MenuItem(MenuName, isValidateFunction: true)]
    private static bool OpenValidateFunction(MenuCommand menuCommand)
    {
        return menuCommand.context != null
            && !(menuCommand.context is Transform);
    }

    private void OnGUI()
    {
        using (new EditorGUI.DisabledScope(_sourceComponent != null))
        {
            _sourceComponent = (Component)EditorGUILayout.ObjectField(SourceComponentDisplayName, _sourceComponent, typeof(Component), allowSceneObjects: true);
        }
        _destinationGameObject = (GameObject)EditorGUILayout.ObjectField(DestinationGameObjectDisplayName, _destinationGameObject, typeof(GameObject), allowSceneObjects: true);
        _replaceReferences = EditorGUILayout.Toggle(ReplaceReferencesDisplayName, _replaceReferences);

        var lockedReason = GetLockedReason();
        using (new EditorGUI.DisabledScope(lockedReason != null))
        {
            if (GUILayout.Button("Move"))
            {
                Execute();
            }
        }

        if (lockedReason != null)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(lockedReason);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
    }

    private void Execute()
    {
        var sourceType = _sourceComponent.GetType();
        _requireComponentOwners = GetRequireComponentOwners(_sourceComponent);
        if (_requireComponentOwners.Count > 0)
        {
            Debug.LogError($"{sourceType.Name} is required by {_requireComponentOwners[0].Name}.");
            return;
        }

        var destinationComponent = Undo.AddComponent(_destinationGameObject, sourceType);
        if (destinationComponent == null)
        {
            Debug.LogError($"AddComponent({sourceType.Name}) failed.");
            return;
        }

        EditorUtility.CopySerialized(_sourceComponent, destinationComponent);
        var references = _replaceReferences ? FindReferences(_sourceComponent) : null;
        if (references != null)
        {
            foreach (var serializedProperty in references)
            {
                serializedProperty.objectReferenceValue = destinationComponent;
                serializedProperty.serializedObject.ApplyModifiedProperties();
            }
        }

        Undo.DestroyObjectImmediate(_sourceComponent);
        Undo.SetCurrentGroupName(UndoName);
        Close();
    }

    private string GetLockedReason()
    {
        if (_sourceComponent == null)
            return "[" + SourceComponentDisplayName + "] is null.";

        var requiredOwner = _requireComponentOwners?.Find(owner => _sourceComponent.GetComponent(owner) != null);
        if (requiredOwner != null)
            return $"{_sourceComponent.GetType().Name} is required by {requiredOwner.Name}.";

        if (_destinationGameObject == null)
            return "[" + DestinationGameObjectDisplayName + "] is null.";

        if (_sourceComponent.gameObject == _destinationGameObject)
            return "[" + DestinationGameObjectDisplayName + "] is the same GameObject.";

        if (_destinationGameObject.GetComponent(_sourceComponent.GetType()) != null && _sourceComponent.GetType().GetCustomAttribute<DisallowMultipleComponent>(inherit: true) != null)
            return  "The same Component already exists.";

        var sourceScene = _sourceComponent.gameObject.scene;
        var destinationScene = _destinationGameObject.scene;
        if (sourceScene != destinationScene)
            return "[" + DestinationGameObjectDisplayName + "] is in a different environment.";

        var sourceAssetPath = AssetDatabase.GetAssetPath(_sourceComponent);
        var destinationAssetPath = AssetDatabase.GetAssetPath(_destinationGameObject);
        if (sourceAssetPath != destinationAssetPath)
            return "[" + DestinationGameObjectDisplayName + "] is in a different environment.";

        return null;
    }

    private static List<SerializedProperty> FindReferences(Component component)
    {
        var gameObject = component.gameObject;
        var scene = gameObject.scene;
        if (scene.IsValid())
            return FindReferencesInScene(scene, component);

        var assetPath = AssetDatabase.GetAssetPath(component);
        if (!string.IsNullOrEmpty(assetPath) && AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) is GameObject prefab)
            return FindReferencesInGameObject(prefab, component);

        return null;
    }

    private static List<SerializedProperty> FindReferencesInScene(Scene scene, Component target)
    {
        var components = new List<Component>();
        var references = new List<SerializedProperty>();
        foreach (var rootGameObject in scene.GetRootGameObjects())
        {
            FindReferencesRecursive(references, rootGameObject, target, components);
        }
        return references;
    }

    private static List<SerializedProperty> FindReferencesInGameObject(GameObject prefab, Component target)
    {
        var references = new List<SerializedProperty>();
        FindReferencesRecursive(references, prefab, target, new List<Component>());
        return references;
    }

    private static void FindReferencesRecursive(in List<SerializedProperty> references, GameObject gameObject, in Component target, in List<Component> components)
    {
        gameObject.GetComponents(components);
        foreach (var component in components)
        {
            if (component is Transform)
                continue;

            var serializedObject = new SerializedObject(component);
            var iterator = serializedObject.GetIterator();
            if (!iterator.Next(enterChildren: true))
            {
                serializedObject.Dispose();
                continue;
            }

            var beforeCount = references.Count;
            do
            {
                if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                {
                    if (ReferenceEquals(iterator.objectReferenceValue, target))
                    {
                        references.Add(iterator.Copy());
                    }
                }
            } while (iterator.Next(enterChildren: ShouldEnterChildren(iterator.propertyType)));

            if (beforeCount == references.Count)
                serializedObject.Dispose();
        }

        foreach (Transform transform in gameObject.transform)
        {
            FindReferencesRecursive(references, transform.gameObject, target, components);
        }
    }

    private static bool ShouldEnterChildren(SerializedPropertyType propertyType)
    {
        switch (propertyType)
        {
            case SerializedPropertyType.Generic:
            case SerializedPropertyType.ManagedReference:
                return true;
            default:
                return false;
        }
    }

    private static List<Type> GetRequireComponentOwners(Component target)
    {
        List<Type> owners = new List<Type>();
        if (target == null)
            return owners;

        var targetType = target.GetType();
        foreach (var component in target.GetComponents<Component>())
        {
            foreach (var requireComponent in component.GetType().GetCustomAttributes<RequireComponent>())
            {
                if (requireComponent.m_Type0 == targetType || requireComponent.m_Type1 == targetType || requireComponent.m_Type2 == targetType)
                {
                    owners.Add(component.GetType());
                    break;
                }
            }
        }
        return owners;
    }
}
#endif
