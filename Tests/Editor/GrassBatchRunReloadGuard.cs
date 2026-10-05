using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

/// <summary>
/// Opt-in protection for the audited Grass-only batch selection. Registration
/// never locks startup import. Admission uses the actual RunStarted tree, not
/// its command-line regex, and leaves the owner's Play options unchanged.
/// </summary>
[InitializeOnLoad]
internal static class GrassBatchRunReloadGuard
{
    internal const string AssemblyName = "com.cone.grass.tests.editor";
    internal const string AssemblyPath = "Packages/com.cone.grass/Tests/Editor/com.cone.grass.tests.editor.asmdef";
    internal const string OptIn = "-grassBatchReloadGuard";
    private static readonly ReloadLease Lease = new ReloadLease(
        EditorApplication.LockReloadAssemblies, EditorApplication.UnlockReloadAssemblies);
    private static TestRunnerApi runner;
    private static RunCallbacks callbacks;

    static GrassBatchRunReloadGuard()
    {
        if (!ShouldRegister(Environment.GetCommandLineArgs(), Application.isBatchMode,
            CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName))
            return;

        callbacks = new RunCallbacks();
        runner = ScriptableObject.CreateInstance<TestRunnerApi>();
        runner.hideFlags = HideFlags.HideAndDontSave;
        runner.RegisterCallbacks(callbacks);
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        CompilationPipeline.compilationStarted += OnCompilationStarted;
        EditorApplication.quitting += Shutdown;
        AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
    }

    internal static bool ShouldRegister(string[] arguments, bool batchMode, Func<string, string> asmdefPathOf)
    {
        if (!batchMode || arguments == null || asmdefPathOf == null)
            return false;
        bool run = false, optIn = false;
        string assemblies = null, platform = null;
        for (int i = 0; i < arguments.Length; i++)
        {
            string argument = arguments[i];
            if (string.Equals(argument, "-runTests", StringComparison.OrdinalIgnoreCase))
                run = true;
            else if (argument == OptIn)
                optIn = true;
            else if (string.Equals(argument, "-assemblyNames", StringComparison.OrdinalIgnoreCase))
            {
                if (assemblies != null || ++i >= arguments.Length) return false;
                assemblies = arguments[i];
            }
            else if (string.Equals(argument, "-testPlatform", StringComparison.OrdinalIgnoreCase))
            {
                if (platform != null || ++i >= arguments.Length) return false;
                platform = arguments[i];
            }
        }
        // An explicit platform and a single exact assembly avoid both implicit
        // whole-project selection and overlap with Kinematica's mixed-run guard.
        return run && optIn && assemblies == AssemblyName &&
            string.Equals(platform, "EditMode", StringComparison.OrdinalIgnoreCase) &&
            IsGrassAssemblyPath(asmdefPathOf(AssemblyName));
    }

    private static bool IsGrassAssemblyPath(string path) => !string.IsNullOrEmpty(path) &&
        string.Equals(path.Replace('\\', '/'), AssemblyPath, StringComparison.Ordinal);

    internal readonly struct SelectedLeaf
    {
        internal readonly MethodInfo Method;
        internal readonly Type Fixture;
        internal readonly object[] Arguments;
        internal readonly string UniqueName;
        internal SelectedLeaf(MethodInfo method, Type fixture, object[] arguments, string uniqueName)
        { Method = method; Fixture = fixture; Arguments = arguments; UniqueName = uniqueName; }
    }

    internal static string MethodKey(MethodInfo method)
    {
        if (method?.DeclaringType == null) return null;
        ParameterInfo[] parameters = method.GetParameters();
        var names = new string[parameters.Length];
        for (int i = 0; i < names.Length; i++) names[i] = parameters[i].ParameterType.FullName;
        return method.DeclaringType.FullName + "." + method.Name + "(" + string.Join(",", names) + ")";
    }

    internal static bool IsReloadSafeSelection(IEnumerable<SelectedLeaf> leaves,
        Func<string, string> asmdefPathOf, bool domainReloadDisabled, bool sceneReloadEnabled)
    {
        // This is a fixed method/signature inventory, not an authored-argument
        // whitelist: same-type values are allowed because the safety audit covers
        // every path through each method and its setup/teardown. Unknown methods
        // still require an explicit source review before admission.
        if (!domainReloadDisabled || !sceneReloadEnabled || leaves == null || asmdefPathOf == null)
            return false;
        int count = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var cases = new HashSet<string>(StringComparer.Ordinal);
        foreach (SelectedLeaf leaf in leaves)
        {
            MethodInfo method = leaf.Method;
            Type type = method?.DeclaringType;
            if (++count > 10000 || type == null || leaf.Fixture != type ||
                type.Assembly != typeof(GrassBatchRunReloadGuard).Assembly ||
                type.Assembly.GetName().Name != AssemblyName || !IsGrassAssemblyPath(asmdefPathOf(AssemblyName)) ||
                !method.IsPublic || method.IsStatic || method.IsAbstract || method.ContainsGenericParameters ||
                string.IsNullOrEmpty(leaf.UniqueName) || !names.Add(leaf.UniqueName))
                return false;
            string key = MethodKey(method);
            if (!GrassBatchRunReloadInventory.Methods.Contains(key)) return false;
            if (method.ReturnType != typeof(void) &&
                !(key == "GrassSettingsLifecycleTests.EnabledOwnerMovedToPreviewStopsRenderingAndReleasesOwnership()" &&
                  method.ReturnType == typeof(IEnumerator)))
                return false;
            ParameterInfo[] parameters = method.GetParameters();
            object[] values = leaf.Arguments ?? Array.Empty<object>();
            if (parameters.Length != values.Length) return false;
            for (int i = 0; i < values.Length; i++)
            {
                object value = values[i];
                Type parameter = parameters[i].ParameterType;
                if (value == null)
                {
                    // Existing modifier cases intentionally have no render tag.
                    // Null is valid only for our one supported reference type.
                    if (parameter != typeof(string)) return false;
                    key += "|" + parameter.FullName + ":null";
                    continue;
                }
                if (value.GetType() != parameter ||
                    !(parameter.IsEnum || parameter == typeof(bool) || parameter == typeof(int) ||
                      parameter == typeof(float) || parameter == typeof(double) || parameter == typeof(string)))
                    return false;
                // Preserve floating-point bit identities (including signed zero)
                // rather than depending on display names or culture formatting.
                string text = value is float single ? BitConverter.SingleToInt32Bits(single).ToString("X8") :
                    value is double number ? BitConverter.DoubleToInt64Bits(number).ToString("X16") :
                    Convert.ToString(value, CultureInfo.InvariantCulture);
                key += "|" + parameter.FullName + ":value:" + text.Length + ":" + text;
            }
            if (!cases.Add(key)) return false;
        }
        return count > 0;
    }

    internal static bool IsReloadSafeTree(ITestAdaptor tree, Func<string, string> asmdefPathOf,
        bool domainReloadDisabled, bool sceneReloadEnabled)
    {
        if (tree == null) return false;
        var leaves = new List<SelectedLeaf>();
        var pending = new Stack<(ITestAdaptor node, TestMode ancestor, bool root)>();
        var visited = new HashSet<ITestAdaptor>();
        pending.Push((tree, TestMode.EditMode, true));
        while (pending.Count != 0)
        {
            var entry = pending.Pop();
            ITestAdaptor node = entry.node;
            if (node == null || visited.Count >= 20000 || !visited.Add(node) ||
                !TryEditMode(node.TestMode, entry.ancestor, entry.root, out TestMode mode))
                return false;
            if (node.IsSuite)
            {
                if (node.Children == null) return false;
                foreach (ITestAdaptor child in node.Children)
                {
                    if (pending.Count + visited.Count >= 20000) return false;
                    pending.Push((child, mode, false));
                }
            }
            else
            {
                if (node.HasChildren) return false;
                leaves.Add(new SelectedLeaf(node.Method?.MethodInfo, node.TypeInfo?.Type,
                    node.Arguments, node.UniqueName));
            }
        }
        return IsReloadSafeSelection(leaves, asmdefPathOf, domainReloadDisabled, sceneReloadEnabled);
    }

    internal static bool TryEditMode(TestMode declared, TestMode ancestor, bool root, out TestMode effective)
    {
        effective = default;
        // TF 1.8 leaves deeply nested case modes unspecified after postorder
        // adaptation. Only proven EditMode ancestry may supply that zero value.
        if (root ? declared != TestMode.EditMode :
            ancestor != TestMode.EditMode || (declared != 0 && declared != TestMode.EditMode))
            return false;
        effective = TestMode.EditMode;
        return true;
    }

    internal sealed class ReloadLease
    {
        private readonly Action take, release;
        private bool readyForEntry;
        internal bool Armed { get; private set; }
        internal ReloadLease(Action take, Action release) { this.take = take; this.release = release; }
        internal bool Start(bool admitted)
        {
            if (!admitted) { Finish(); return false; }
            if (Armed) return false;
            take(); Armed = true; readyForEntry = true; return true;
        }
        internal void ExitingEditMode()
        {
            if (!Armed || !readyForEntry) return;
            // The Test Framework's EnterPlayMode released one lock immediately
            // before this event. Replace that release; this is not another
            // outstanding owned lock at RunFinished (same contract as Kin #938).
            take(); readyForEntry = false;
        }
        internal void EnteredEditMode() { if (Armed) readyForEntry = true; }
        internal bool Finish()
        {
            if (!Armed) return false;
            Armed = false; readyForEntry = false; release(); return true;
        }
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode) Lease.ExitingEditMode();
        else if (state == PlayModeStateChange.EnteredEditMode) Lease.EnteredEditMode();
    }

    private static void OnCompilationStarted(object context)
    {
        if (Lease.Armed)
            Debug.LogWarning("[GrassBatchRunReloadGuard] Compilation requested during the audited run; " +
                "assembly reload is deferred until completion. This run continues using its starting assemblies.");
    }

    private static void Release(string reason)
    {
        if (Lease.Finish()) Debug.Log("[GrassBatchRunReloadGuard] " + reason + ": released owned reload lock.");
    }

    private static void Shutdown()
    {
        try { Release("Editor shutdown/reload"); }
        finally
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            EditorApplication.quitting -= Shutdown;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            if (runner)
            {
                runner.UnregisterCallbacks(callbacks);
                UnityEngine.Object.DestroyImmediate(runner);
            }
            runner = null;
            callbacks = null;
        }
    }

    private sealed class RunCallbacks : IErrorCallbacks
    {
        public void RunStarted(ITestAdaptor testsToRun)
        {
            bool admitted = false;
            try
            {
                bool options = EditorSettings.enterPlayModeOptionsEnabled;
                admitted = IsReloadSafeTree(testsToRun,
                    CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName,
                    options && (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0,
                    !options || (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableSceneReload) == 0);
            }
            catch (Exception error)
            {
                Debug.LogWarning("[GrassBatchRunReloadGuard] Refused malformed selection: " + error.Message);
            }
            if (Lease.Start(admitted))
                Debug.Log("[GrassBatchRunReloadGuard] RunStarted: acquired owned reload lock after typed Grass selection admission.");
            else if (!admitted)
                Debug.LogWarning("[GrassBatchRunReloadGuard] Refused selection: only audited Grass methods with " +
                    "domain reload disabled and scene reload enabled may be protected. Editor settings were not changed.");
        }
        public void RunFinished(ITestResultAdaptor result) { Release("RunFinished"); }
        public void OnError(string message) { Release("RunError"); }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) { }
    }
}
