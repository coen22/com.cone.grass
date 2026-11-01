using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Cone.Grass.Tests
{
    /// <summary>
    /// Unity never serializes a readonly field, so a readonly serialized setting silently loses its scene and asset data.
    /// Every field Unity would serialize in the package's assemblies must stay writable.
    /// </summary>
    public sealed class GrassSerializedFieldTests
    {
        private static readonly string[] s_assemblies = { "Cone.Grass", "Cone.Grass.Editor" };

        [Test]
        public void NoSerializedFieldIsReadOnly()
        {
            var offenders = new List<string>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()
                         .Where(a => s_assemblies.Contains(a.GetName().Name)))
            {
                foreach (Type type in assembly.GetTypes())
                {
                    bool serializedType = typeof(UnityEngine.Object).IsAssignableFrom(type) ||
                        type.IsDefined(typeof(SerializableAttribute), false);
                    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly;
                    foreach (FieldInfo field in type.GetFields(Flags))
                    {
                        if (!field.IsInitOnly || field.IsDefined(typeof(NonSerializedAttribute), false))
                        {
                            continue;
                        }

                        bool marked = field.IsDefined(typeof(SerializeField), false) ||
                            field.IsDefined(typeof(SerializeReference), false);
                        if (marked || serializedType && field.IsPublic)
                        {
                            offenders.Add($"{type.FullName}.{field.Name}");
                        }
                    }
                }
            }

            Assert.That(s_assemblies.All(name => AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == name)),
                "The package assemblies were not loaded, so the check would pass without looking.");
            Assert.That(offenders, Is.Empty, "Readonly fields are never serialized: " + string.Join(", ", offenders));
        }
    }
}
