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
    private bool copied;
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
        copyPath = AssetDatabase.GenerateUniqueAssetPath("Assets/GrassSavedScene_" + Guid.NewGuid().ToString("N") + ".unity");
        Assert.That(AssetDatabase.AssetPathToGUID(copyPath), Is.Empty, $"Acquire copy '{copyPath}': path must be unused.");
        try
        {
            copied = During($"Copy template '{templatePath}' to '{copyPath}'", () => AssetDatabase.CopyAsset(templatePath, copyPath));
            Assert.That(copied, Is.True, $"Acquire copy '{copyPath}': only a successfully created unique copy is owned for cleanup.");
            Source = OpenOwnedScene(copyPath);
            AssertEmptyNormalScene(Source, copyPath);
            if (includeTarget)
            {
                Target = OpenOwnedScene(templatePath);
                AssertEmptyNormalScene(Target, templatePath);
                Assert.That(Source, Is.Not.EqualTo(Target), $"Acquire '{copyPath}': source and readonly target must be distinct.");
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

    private Scene OpenOwnedScene(string path)
    {
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
        Assert.That(scene.rootCount, Is.Zero, $"Inspect '{path}': the readonly fixture contains no GameObjects or scripts.");
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

    public void Dispose()
    {
        var failures = new List<Exception>();
        Attempt(failures, "Restore prior active scene", RestorePriorActive);
        Attempt(failures, $"Close readonly target '{templatePath}'", () =>
        {
            if (Target.IsValid())
            {
                Assert.That(Target.path, Is.EqualTo(templatePath), $"Close target '{templatePath}': verify owned path.");
                Assert.That(EditorSceneManager.CloseScene(Target, true), Is.True, $"Close target '{templatePath}': CloseScene must succeed.");
                Target = default;
            }
        });
        Attempt(failures, $"Close source '{copyPath}'", CloseSource);
        Attempt(failures, $"Delete owned copy '{copyPath}'", () =>
        {
            bool remainingCopyScene = SceneManager.GetSceneByPath(copyPath).IsValid();
            if (copied && !remainingCopyScene)
            {
                Assert.That(AssetDatabase.DeleteAsset(copyPath), Is.True, $"Delete owned copy '{copyPath}': DeleteAsset must succeed.");
                copied = false;
            }
            Assert.That(remainingCopyScene, Is.False,
                $"Delete owned copy '{copyPath}': an uncertain or still loaded copy is retained for diagnosis, not deleted.");
        });
        Attempt(failures, $"Verify readonly template '{templatePath}'", () =>
        {
            Assert.That(SceneManager.GetSceneByPath(templatePath).IsValid(), Is.False, $"Verify template '{templatePath}': no owned target may remain.");
            Assert.That(AssetDatabase.GetAssetDependencyHash(templatePath), Is.EqualTo(templateHash),
                $"Verify template '{templatePath}': opening it must never save or rewrite its package asset.");
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
