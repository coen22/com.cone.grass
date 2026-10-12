using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Owns saved test-scene copies without saving or recreating the user's scene setup.</summary>
internal sealed class GrassSavedSceneFixture : IDisposable
{
    private const string TemplateGuid = "c0293ca4b83f495b84033719ef793631";
    private readonly Scene priorActive;
    private readonly PriorScene[] priorScenes;
    private readonly SceneSetup[] priorSetup;
    private readonly string templatePath;
    private readonly Hash128 templateHash;
    private readonly string copyPath;
    private readonly string targetCopyPath;
    private string copyGuid, targetCopyGuid;
    private bool copied, targetCopied;
    public Scene Source { get; private set; }
    public Scene Target { get; private set; }

    private readonly struct PriorScene
    {
        public readonly Scene Scene;
        public readonly string Path, Name;
        public readonly bool Loaded, Dirty;
        public readonly GameObject[] Roots;

        public PriorScene(Scene scene)
        {
            Scene = scene; Path = scene.path; Name = scene.name;
            Loaded = scene.isLoaded; Dirty = scene.isDirty;
            Roots = scene.isLoaded ? scene.GetRootGameObjects() : Array.Empty<GameObject>();
        }
    }

    public GrassSavedSceneFixture(bool includeTarget = false)
    {
        Assert.That(EditorApplication.isPlaying, Is.False, "Acquire: this scene control retains its Edit-only domain.");
        priorActive = SceneManager.GetActiveScene();
        priorSetup = EditorSceneManager.GetSceneManagerSetup();
        priorScenes = new PriorScene[SceneManager.sceneCount];
        for (int i = 0; i < priorScenes.Length; i++)
            priorScenes[i] = new PriorScene(SceneManager.GetSceneAt(i));
        templatePath = AssetDatabase.GUIDToAssetPath(TemplateGuid);
        Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(templatePath), Is.Not.Null,
            $"Acquire template '{templatePath}' from GUID {TemplateGuid}.");
        Assert.That(SceneManager.GetSceneByPath(templatePath).IsValid(), Is.False,
            $"Acquire template '{templatePath}': never take ownership of a scene already present in the user's hierarchy.");
        templateHash = AssetDatabase.GetAssetDependencyHash(templatePath);
        copyPath = NewCopyPath();
        targetCopyPath = includeTarget ? NewCopyPath() : null;
        try
        {
            copyGuid = CopyTemplate(copyPath, ref copied);
            Source = OpenOwnedScene(copyPath);
            AssertEmptyNormalScene(Source, copyPath);
            if (includeTarget)
            {
                // Git/registry packages are read-only: even opening their scene
                // template is rejected by Unity. Both loaded scenes must be
                // independent writable copies; the package asset is only read.
                targetCopyGuid = CopyTemplate(targetCopyPath, ref targetCopied);
                Assert.That(targetCopyGuid, Is.Not.EqualTo(copyGuid),
                    "Acquire target: source and target must own distinct asset identities.");
                Target = OpenOwnedScene(targetCopyPath);
                AssertEmptyNormalScene(Target, targetCopyPath);
                Assert.That(Source, Is.Not.EqualTo(Target), $"Acquire '{copyPath}': source and target copies must be distinct.");
            }
            Assert.That(SceneManager.sceneCount, Is.EqualTo(priorScenes.Length + (includeTarget ? 2 : 1)),
                $"Acquire '{copyPath}': only the requested owned scenes may be added.");
            Assert.That(During($"Activate source '{copyPath}'", () => SceneManager.SetActiveScene(Source)), Is.True,
                $"Activate source '{copyPath}': SetActiveScene must succeed.");
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(Source), $"Activate source '{copyPath}': verify actual active scene.");
        }
        catch (Exception bodyError)
        {
            try { Dispose(); }
            catch (Exception cleanupError)
            { throw new AggregateException($"Scene acquisition/owned cleanup failures retained for '{copyPath}'.", bodyError, cleanupError); }
            throw;
        }
    }

    private static string NewCopyPath()
    {
        return AssetDatabase.GenerateUniqueAssetPath("Assets/GrassSavedScene_" + Guid.NewGuid().ToString("N") + ".unity");
    }

    private static string ExistingAssetGuid(string path)
    {
        // The default lookup also returns GUIDs of assets deleted during this
        // Editor session. Ownership and removal checks require live assets.
        return AssetDatabase.AssetPathToGUID(path, AssetPathToGUIDOptions.OnlyExistingAssets);
    }

    private string CopyTemplate(string path, ref bool owned)
    {
        Assert.That(path.StartsWith("Assets/", StringComparison.Ordinal), Is.True,
            $"Acquire copy '{path}': only writable project assets may be created.");
        Assert.That(ExistingAssetGuid(path), Is.Empty, $"Acquire copy '{path}': path must be unused.");
        owned = During($"Copy template '{templatePath}' to '{path}'", () => AssetDatabase.CopyAsset(templatePath, path));
        Assert.That(owned, Is.True, $"Acquire copy '{path}': only a successfully created unique copy is owned for cleanup.");
        string guid = ExistingAssetGuid(path);
        Assert.That(string.IsNullOrEmpty(guid), Is.False, $"Acquire copy '{path}': record its actual asset identity.");
        Assert.That(guid, Is.Not.EqualTo(TemplateGuid), $"Acquire copy '{path}': the package template must never become an owned asset.");
        return guid;
    }

    private static void AssertOwnedAsset(string path, string guid)
    {
        Assert.That(path.StartsWith("Assets/", StringComparison.Ordinal), Is.True,
            $"Inspect owned asset '{path}': the package template is not writable fixture storage.");
        Assert.That(string.IsNullOrEmpty(guid), Is.False, $"Inspect owned asset '{path}': acquisition must have recorded an identity.");
        Assert.That(ExistingAssetGuid(path), Is.EqualTo(guid),
            $"Inspect owned asset '{path}': never overwrite or delete an asset replaced at the same path.");
    }

    private Scene OpenOwnedScene(string path)
    {
        bool isSource = copied && path == copyPath;
        bool isTarget = targetCopied && path == targetCopyPath;
        Assert.That(isSource || isTarget, Is.True, $"Open '{path}': require a successfully acquired fixture copy.");
        AssertOwnedAsset(path, isSource ? copyGuid : targetCopyGuid);
        Assert.That(SceneManager.GetSceneByPath(path).IsValid(), Is.False,
            $"Open '{path}': never take ownership of an already loaded or uncertain scene.");
        Scene opened = During($"Open owned scene '{path}' additively", () => EditorSceneManager.OpenScene(path, OpenSceneMode.Additive));
        // A failed/ambiguous return must never grant ownership of an old
        // scene. Leave any unproven partial acquisition for diagnosis.
        Assert.That(opened.IsValid(), Is.True, $"Open '{path}': returned scene must be valid.");
        Assert.That(opened.path, Is.EqualTo(path), $"Open '{path}': returned scene must match the requested asset.");
        foreach (PriorScene previous in priorScenes)
            Assert.That(opened, Is.Not.EqualTo(previous.Scene), $"Open '{path}': must not acquire prior scene '{previous.Path}' ({previous.Name}).");
        return opened;
    }

    private static void AssertEmptyNormalScene(Scene scene, string path)
    {
        Assert.That(scene.IsValid() && scene.isLoaded, Is.True, $"Inspect '{path}': scene must be valid and loaded.");
        Assert.That(scene.path, Is.EqualTo(path), $"Inspect '{path}': scene must retain its owned asset path.");
        Assert.That(EditorSceneManager.IsPreviewScene(scene), Is.False,
            $"Inspect '{path}': the control requires a normal loaded scene.");
        Assert.That(scene.rootCount, Is.Zero, $"Inspect '{path}': the copied fixture contains no GameObjects or scripts.");
    }

    public void RestorePriorActive()
    {
        Assert.That(priorActive.IsValid() && priorActive.isLoaded, Is.True,
            $"Restore active scene for '{copyPath}': the prior scene must remain valid and loaded.");
        if (SceneManager.GetActiveScene() != priorActive)
            Assert.That(During($"Restore active scene '{priorActive.path}'", () => SceneManager.SetActiveScene(priorActive)), Is.True,
                $"Restore active scene '{priorActive.path}': SetActiveScene must succeed.");
        Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(priorActive),
            $"Restore active scene '{priorActive.path}': verify actual active scene even when no switch was needed.");
    }

    public void SaveAndCloseSource()
    {
        Assert.That(copied && Source.IsValid() && Source.isLoaded, Is.True,
            $"Save source '{copyPath}': require a successfully copied, valid loaded source.");
        Assert.That(Source.path, Is.EqualTo(copyPath), $"Save source '{copyPath}': only the fixture-owned copy may be saved.");
        AssertOwnedAsset(copyPath, copyGuid);
        RestorePriorActive();
        Assert.That(During($"Save source '{copyPath}'", () => EditorSceneManager.SaveScene(Source, copyPath)), Is.True,
            $"Save source '{copyPath}': SaveScene must succeed.");
        CloseSource();
    }

    public Scene ReopenSource()
    {
        Assert.That(copied, Is.True, $"Reopen source '{copyPath}': the fixture must own the copied asset.");
        Assert.That(Source.IsValid(), Is.False, $"Reopen source '{copyPath}': close the previous source first.");
        Assert.That(SceneManager.GetSceneByPath(copyPath).IsValid(), Is.False,
            $"Reopen source '{copyPath}': do not take ownership of an already loaded or uncertain scene.");
        Source = OpenOwnedScene(copyPath);
        return Source;
    }

    public void CloseSource()
    {
        if (Source.IsValid())
        {
            Assert.That(Source.path, Is.EqualTo(copyPath), $"Close source '{copyPath}': verify owned path before closing.");
            RestorePriorActive();
            Assert.That(During($"Close source '{copyPath}'", () => EditorSceneManager.CloseScene(Source, true)), Is.True,
                $"Close source '{copyPath}': CloseScene must succeed.");
            Source = default;
        }
        Assert.That(SceneManager.GetSceneByPath(copyPath).IsValid(), Is.False,
            $"Close source '{copyPath}': no loaded or uncertain copy may remain.");
    }

    public Scene SaveAndReopenSource()
    {
        SaveAndCloseSource();
        return ReopenSource();
    }

    private static void DeleteOwnedCopy(string path, string guid, ref bool owned)
    {
        if (path == null)
            return;
        bool remainingCopyScene = SceneManager.GetSceneByPath(path).IsValid();
        Assert.That(remainingCopyScene, Is.False,
            $"Delete owned copy '{path}': an uncertain or still loaded copy is retained for diagnosis, not deleted.");
        if (!owned)
            return;
        AssertOwnedAsset(path, guid);
        Assert.That(AssetDatabase.DeleteAsset(path), Is.True, $"Delete owned copy '{path}': DeleteAsset must succeed.");
        owned = false;
        Assert.That(ExistingAssetGuid(path), Is.Empty, $"Delete owned copy '{path}': verify asset removal.");
    }

    public void Dispose()
    {
        var failures = new List<Exception>();
        Attempt(failures, "Restore prior active scene", RestorePriorActive);
        Attempt(failures, $"Close target copy '{targetCopyPath}'", () =>
        {
            if (Target.IsValid())
            {
                Assert.That(Target.path, Is.EqualTo(targetCopyPath), $"Close target '{targetCopyPath}': verify owned path.");
                Assert.That(EditorSceneManager.CloseScene(Target, true), Is.True, $"Close target '{targetCopyPath}': CloseScene must succeed.");
                Target = default;
            }
        });
        Attempt(failures, $"Close source '{copyPath}'", CloseSource);
        Attempt(failures, $"Delete owned target copy '{targetCopyPath}'", () => DeleteOwnedCopy(targetCopyPath, targetCopyGuid, ref targetCopied));
        Attempt(failures, $"Delete owned source copy '{copyPath}'", () => DeleteOwnedCopy(copyPath, copyGuid, ref copied));
        Attempt(failures, $"Verify readonly template '{templatePath}'", () =>
        {
            Assert.That(SceneManager.GetSceneByPath(templatePath).IsValid(), Is.False, $"Verify template '{templatePath}': the template must never be opened.");
            Assert.That(AssetDatabase.GetAssetDependencyHash(templatePath), Is.EqualTo(templateHash),
                $"Verify template '{templatePath}': copying it must never save or rewrite its package asset.");
        });
        Attempt(failures, "Verify prior active scene and scene count", () =>
        {
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(priorActive), "Verify prior setup: active scene must be unchanged.");
            Assert.That(SceneManager.sceneCount, Is.EqualTo(priorScenes.Length), "Verify prior setup: scene count must be unchanged.");
        });
        for (int i = 0; i < priorScenes.Length; i++)
        {
            int index = i;
            PriorScene previous = priorScenes[i];
            string phase = $"Verify prior scene {index} '{previous.Path}' ({previous.Name})";
            Attempt(failures, phase, () =>
            {
                Scene current = SceneManager.GetSceneAt(index);
                Assert.That(current, Is.EqualTo(previous.Scene), phase + ": identity/order.");
                Assert.That(current.path, Is.EqualTo(previous.Path), phase + ": path.");
                Assert.That(current.name, Is.EqualTo(previous.Name), phase + ": name.");
                Assert.That(current.isLoaded, Is.EqualTo(previous.Loaded), phase + ": loaded state.");
                Assert.That(current.isDirty, Is.EqualTo(previous.Dirty), phase + ": dirty state.");
                if (current.isLoaded)
                    Assert.That(current.GetRootGameObjects(), Is.EqualTo(previous.Roots), phase + ": root objects/order.");
            });
        }
        Attempt(failures, "Verify prior scene-manager setup", () =>
        {
            SceneSetup[] after = EditorSceneManager.GetSceneManagerSetup();
            Assert.That(after.Length, Is.EqualTo(priorSetup.Length), "Verify scene-manager setup: entry count.");
            for (int i = 0; i < after.Length; i++)
            {
                string phase = $"Verify scene-manager entry {i} '{priorSetup[i].path}'";
                Assert.That(after[i].path, Is.EqualTo(priorSetup[i].path), phase + ": path/order.");
                Assert.That(after[i].isLoaded, Is.EqualTo(priorSetup[i].isLoaded), phase + ": loaded state.");
                Assert.That(after[i].isActive, Is.EqualTo(priorSetup[i].isActive), phase + ": active state.");
            }
        });
        if (failures.Count > 0)
            throw new AggregateException($"Owned-scene cleanup/state checks failed for '{copyPath}'; uncertain artifacts retained.", failures);
    }

    private static T During<T>(string phase, Func<T> operation)
    {
        try { return operation(); }
        catch (Exception error) { throw new InvalidOperationException(phase, error); }
    }

    private static void Attempt(List<Exception> failures, string phase, Action operation)
    {
        try { operation(); }
        catch (Exception error) { failures.Add(new InvalidOperationException(phase, error)); }
    }
}
