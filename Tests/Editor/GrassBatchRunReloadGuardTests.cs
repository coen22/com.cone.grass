using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.TestTools.TestRunner.Api;

/// <summary>Policy and lease tests use no real Editor reload locks or Play transitions.</summary>
public sealed class GrassBatchRunReloadGuardTests
{
    private static string OwnedPath(string assembly) => assembly == GrassBatchRunReloadGuard.AssemblyName
        ? GrassBatchRunReloadGuard.AssemblyPath : null;

    private static string[] Command(params string[] suffix) => new[]
    {
        "Unity", "-batchmode", "-runTests", GrassBatchRunReloadGuard.OptIn,
        "-assemblyNames", GrassBatchRunReloadGuard.AssemblyName, "-testPlatform", "EditMode"
    }.Concat(suffix).ToArray();

    private static GrassBatchRunReloadGuard.SelectedLeaf Leaf(Type fixture, string method,
        string name, params object[] arguments) => new GrassBatchRunReloadGuard.SelectedLeaf(
            fixture.GetMethod(method, BindingFlags.Instance | BindingFlags.Public), fixture, arguments, name);

    private static bool Admitted(params GrassBatchRunReloadGuard.SelectedLeaf[] leaves) =>
        GrassBatchRunReloadGuard.IsReloadSafeSelection(leaves, OwnedPath, true, true);

    [Test]
    public void RegistrationRequiresExplicitBatchOptInAndTheSingleOwnedEditModeAssembly()
    {
        Assert.That(GrassBatchRunReloadGuard.ShouldRegister(Command(), true, OwnedPath), Is.True);
        Assert.That(GrassBatchRunReloadGuard.ShouldRegister(Command(), false, OwnedPath), Is.False);
        foreach (string required in new[] { "-runTests", GrassBatchRunReloadGuard.OptIn, "-testPlatform", "-assemblyNames" })
        {
            string[] missing = Command().Where(value => value != required).ToArray();
            Assert.That(GrassBatchRunReloadGuard.ShouldRegister(missing, true, OwnedPath), Is.False, required);
        }
        Assert.That(GrassBatchRunReloadGuard.ShouldRegister(Command("-assemblyNames", "other"), true, OwnedPath), Is.False);
        Assert.That(GrassBatchRunReloadGuard.ShouldRegister(Command("-testPlatform", "PlayMode"), true, OwnedPath), Is.False);
        string[] mixed = Command();
        mixed[5] += ";other.tests";
        Assert.That(GrassBatchRunReloadGuard.ShouldRegister(mixed, true, OwnedPath), Is.False);
        Assert.That(GrassBatchRunReloadGuard.ShouldRegister(Command(), true, _ => "Assets/copied.asmdef"), Is.False);
        Assert.That(GrassBatchRunReloadGuard.ShouldRegister(Command(), true,
            _ => GrassBatchRunReloadGuard.AssemblyPath.Replace('/', '\\')), Is.True);
    }

    [Test]
    public void AdmissionUsesOwnedMethodIdentityAndRefusesUnknownOrMixedSelections()
    {
        var known = Leaf(typeof(GrassDispatchMathTests), nameof(GrassDispatchMathTests.InvalidBoundsDoNotReachIntegerConversion), "known");
        Assert.That(Admitted(known), Is.True);
        Assert.That(Admitted(), Is.False);
        Assert.That(Admitted(known, new GrassBatchRunReloadGuard.SelectedLeaf(
            typeof(object).GetMethod(nameof(ToString)), typeof(object), null, "foreign")), Is.False);
        Assert.That(Admitted(known, Leaf(typeof(GrassBatchRunReloadGuardTests), nameof(UnauditedHelper), "unknown")), Is.False);
        Assert.That(Admitted(new GrassBatchRunReloadGuard.SelectedLeaf(
            known.Method, typeof(GrassBatchRunReloadGuardTests), null, "forged fixture")), Is.False);
        Assert.That(GrassBatchRunReloadGuard.IsReloadSafeSelection(new[] { known }, _ => "Assets/copied.asmdef", true, true), Is.False);
        Assert.That(GrassBatchRunReloadGuard.IsReloadSafeSelection(new[] { known }, OwnedPath, false, true), Is.False);
        Assert.That(GrassBatchRunReloadGuard.IsReloadSafeSelection(new[] { known }, OwnedPath, true, false), Is.False);
    }

    // A public same-assembly helper deliberately omitted from the fixed inventory.
    public void UnauditedHelper() { }

    [Test]
    public void AdmissionChecksTypedArgumentsAndRejectsDuplicateCaseIdentities()
    {
        var first = Leaf(typeof(GrassContactRenderingTests),
            nameof(GrassContactRenderingTests.VisibleSurfacesCastContactsWhenGrassParticipates), "first",
            false, GrassContactRenderingTests.ContactLayout.SceneToGrass);
        var second = Leaf(typeof(GrassContactRenderingTests), first.Method.Name, "second",
            true, GrassContactRenderingTests.ContactLayout.SceneToGrass);
        Assert.That(Admitted(first, second), Is.True);
        Assert.That(Admitted(first, first), Is.False, "Repeated unique node identity must not disguise a malformed tree.");
        Assert.That(Admitted(first, Leaf(typeof(GrassContactRenderingTests), first.Method.Name, "renamed duplicate",
            false, GrassContactRenderingTests.ContactLayout.SceneToGrass)), Is.False);
        Assert.That(Admitted(Leaf(typeof(GrassContactRenderingTests), first.Method.Name, "wrong enum",
            false, (int)GrassContactRenderingTests.ContactLayout.SceneToGrass)), Is.False);
        Assert.That(Admitted(Leaf(typeof(GrassContactRenderingTests), first.Method.Name, "missing", false)), Is.False);
        Assert.That(Admitted(Leaf(typeof(GrassContactRenderingTests), first.Method.Name, "null", false, null)), Is.False);
        Assert.That(Admitted(Leaf(typeof(GrassContactRenderingTests), first.Method.Name, "null bool",
            null, GrassContactRenderingTests.ContactLayout.SceneToGrass)), Is.False);
        Assert.That(Admitted(Leaf(typeof(GrassDispatchMathTests), nameof(GrassDispatchMathTests.InvalidSpacingDoesNotProduceDispatch),
            "float", float.NaN)), Is.True, "Authored invalid-number cases remain selectable.");
        Assert.That(Admitted(Leaf(typeof(GrassDispatchMathTests), nameof(GrassDispatchMathTests.InvalidSpacingDoesNotProduceDispatch),
            "double mismatch", double.NaN)), Is.False);
        const string modifierMethod = nameof(GrassRendererLifecycleTests.ModifierPassLookupPreservesNamesAndTagsWithoutSteadyStateAllocations);
        var color = Leaf(typeof(GrassRendererLifecycleTests), modifierMethod, "authored null color",
            "InfiniteGrass/Modifiers/GrassMaskShader", "GrassColor", null);
        var slope = Leaf(typeof(GrassRendererLifecycleTests), modifierMethod, "authored null slope",
            "InfiniteGrass/Modifiers/GrassMaskShader", "GrassSlope", null);
        Assert.That(Admitted(color, slope), Is.True, "Both existing null-string modifier cases must remain selectable.");
        Assert.That(Admitted(color,
            Leaf(typeof(GrassRendererLifecycleTests), modifierMethod, "empty tag", "InfiniteGrass/Modifiers/GrassMaskShader", "GrassColor", ""),
            Leaf(typeof(GrassRendererLifecycleTests), modifierMethod, "literal null tag", "InfiniteGrass/Modifiers/GrassMaskShader", "GrassColor", "null")), Is.True,
            "Null, empty and literal null strings have different case identities.");
    }

    [Test]
    public void UnspecifiedNestedModesRequireProvenEditModeAncestry()
    {
        Assert.That(GrassBatchRunReloadGuard.TryEditMode(TestMode.EditMode, 0, true, out TestMode parent), Is.True);
        for (int depth = 0; depth < 4; depth++)
        {
            Assert.That(GrassBatchRunReloadGuard.TryEditMode(0, parent, false, out TestMode child), Is.True);
            parent = child;
        }
        Assert.That(parent, Is.EqualTo(TestMode.EditMode));
        Assert.That(GrassBatchRunReloadGuard.TryEditMode(0, TestMode.EditMode, true, out _), Is.False);
        Assert.That(GrassBatchRunReloadGuard.TryEditMode(0, TestMode.PlayMode, false, out _), Is.False);
        Assert.That(GrassBatchRunReloadGuard.TryEditMode(TestMode.PlayMode, TestMode.EditMode, false, out _), Is.False);
        Assert.That(GrassBatchRunReloadGuard.IsReloadSafeTree(null, OwnedPath, true, true), Is.False);
    }

    [Test]
    public void LeaseBalancesThreeRunnerPlayEntriesWithoutTakingAStartupLock()
    {
        int held = 0, takes = 0, releases = 0;
        var lease = new GrassBatchRunReloadGuard.ReloadLease(() => { held++; takes++; }, () => { held--; releases++; });
        Assert.That(held, Is.Zero);
        lease.ExitingEditMode();
        lease.EnteredEditMode();
        Assert.That(lease.Finish(), Is.False);
        Assert.That(takes, Is.Zero, "Registration and unrelated startup events must not lock compilation.");
        Assert.That(lease.Start(true), Is.True);
        Assert.That(lease.Start(true), Is.False);
        for (int session = 0; session < 3; session++)
        {
            held--; // EnterPlayMode's runner unlock precedes ExitingEditMode.
            lease.ExitingEditMode();
            lease.ExitingEditMode(); // Duplicate notification must not leak another lock.
            Assert.That(held, Is.EqualTo(1));
            lease.EnteredEditMode();
        }
        Assert.That(lease.Finish(), Is.True);
        Assert.That(lease.Finish(), Is.False);
        Assert.That(held, Is.Zero);
        Assert.That(takes, Is.EqualTo(4));
        Assert.That(releases, Is.EqualTo(1), "The runner consumed the three replacement locks itself.");
    }

    [Test]
    public void RefusalAndTerminalReleaseRetireOnlyTheOwnedLease()
    {
        int held = 2; // Simulate unrelated owner locks; never release them.
        var lease = new GrassBatchRunReloadGuard.ReloadLease(() => held++, () => held--);
        Assert.That(lease.Start(false), Is.False);
        Assert.That(held, Is.EqualTo(2));
        Assert.That(lease.Start(true), Is.True);
        Assert.That(lease.Start(false), Is.False);
        Assert.That(lease.Armed, Is.False);
        Assert.That(held, Is.EqualTo(2));
        Assert.That(lease.Start(true), Is.True);
        Assert.That(lease.Finish(), Is.True); // Same release used by error, completion and shutdown.
        Assert.That(lease.Finish(), Is.False);
        lease.ExitingEditMode();
        Assert.That(held, Is.EqualTo(2));
    }

    [Test]
    public void EveryCurrentCoreTestMethodHasAnExplicitReviewedInventoryEntry()
    {
        int found = 0;
        foreach (Type type in typeof(GrassBatchRunReloadGuardTests).Assembly.GetTypes())
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!method.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "NUnit.Framework.TestAttribute" ||
                attribute.AttributeType.FullName == "NUnit.Framework.TestCaseAttribute" ||
                attribute.AttributeType.FullName == "UnityEngine.TestTools.UnityTestAttribute")) continue;
            found++;
            Assert.That(GrassBatchRunReloadInventory.Methods.Contains(GrassBatchRunReloadGuard.MethodKey(method)), Is.True,
                "Audit this method and its setup/teardown for C# reload waits before adding it to the fixed inventory: " + method);
        }
        Assert.That(found, Is.GreaterThan(270), "A copied or partial assembly is not the audited suite.");
    }
}
