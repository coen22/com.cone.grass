using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Cone.Grass.Tests
{
    /// <summary>Settings, defaults and level resolution of <see cref="GrassWaterLine"/>.</summary>
    public sealed class GrassWaterLineTests
    {
        private sealed class FixedSource : IGrassWaterLevelSource
        {
            public bool Available = true;
            public float Level;
            public int Calls;

            public bool TryGetWaterLevel(out float level)
            {
                Calls++;
                level = Level;
                return Available;
            }
        }

        private sealed class ThrowingSource : IGrassWaterLevelSource
        {
            public bool TryGetWaterLevel(out float level) => throw new InvalidOperationException("source failed");
        }

        private sealed class SourceBehaviour : MonoBehaviour, IGrassWaterLevelSource
        {
            public bool TryGetWaterLevel(out float level)
            {
                level = 4f;
                return true;
            }
        }

        [Test]
        public void DefaultsAreOffWithAHalfMetreShoreline()
        {
            var waterLine = new GrassWaterLine();
            Assert.That(waterLine.Enabled, Is.False);
            Assert.That(waterLine.Level, Is.Zero);
            Assert.That(waterLine.ShoreFade, Is.EqualTo(0.5f));
            Assert.That(waterLine.Source, Is.Null);
            Assert.That(waterLine.IsActive, Is.False);
            Assert.That(waterLine.TryResolve(out _, out _), Is.False, "Off by default: placement is unchanged.");
        }

        [Test]
        public void RendererCarriesAnInactiveWaterLineByDefault()
        {
            var go = new GameObject("Grass Water Line Test");
            try
            {
                go.SetActive(false);
                InfiniteGrassRenderer renderer = go.AddComponent<InfiniteGrassRenderer>();
                Assert.That(renderer.WaterLine, Is.Not.Null);
                Assert.That(renderer.WaterLine.IsActive, Is.False);

                renderer.WaterLine = null;
                Assert.That(renderer.WaterLine, Is.Not.Null, "Assigning null restores a default water line.");
                var custom = new GrassWaterLine { Enabled = true, Level = 3f };
                renderer.WaterLine = custom;
                Assert.That(renderer.WaterLine, Is.SameAs(custom));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void EnabledLineResolvesItsOwnLevelAndBand()
        {
            var waterLine = new GrassWaterLine { Enabled = true, Level = 12.5f, ShoreFade = 0.75f };
            Assert.That(waterLine.TryResolve(out float level, out float fade), Is.True);
            Assert.That(level, Is.EqualTo(12.5f));
            Assert.That(fade, Is.EqualTo(0.75f));
        }

        [Test]
        public void NegativeOrNonFiniteBandBecomesAHardLine()
        {
            var waterLine = new GrassWaterLine { Enabled = true, ShoreFade = -2f };
            Assert.That(waterLine.TryResolve(out _, out float fade), Is.True);
            Assert.That(fade, Is.Zero);
            waterLine.ShoreFade = float.NaN;
            Assert.That(waterLine.TryResolve(out _, out fade), Is.True);
            Assert.That(fade, Is.Zero);
        }

        [Test]
        public void NonFiniteLevelDisablesTheLine()
        {
            var waterLine = new GrassWaterLine { Enabled = true, Level = float.PositiveInfinity };
            Assert.That(waterLine.TryResolve(out _, out _), Is.False);
        }

        [Test]
        public void SourceTurnsTheLineOnAndOverridesTheLevel()
        {
            var source = new FixedSource { Level = -3f };
            var waterLine = new GrassWaterLine { Level = 8f, Source = source };
            Assert.That(waterLine.IsActive, Is.True, "A source alone turns the water line on.");
            Assert.That(waterLine.TryResolve(out float level, out _), Is.True);
            Assert.That(level, Is.EqualTo(-3f));
            Assert.That(source.Calls, Is.EqualTo(1));
        }

        [Test]
        public void UnavailableSourceFallsBackToTheLevelOnlyWhenEnabled()
        {
            var source = new FixedSource { Available = false, Level = 99f };
            var waterLine = new GrassWaterLine { Level = 2f, Source = source };
            Assert.That(waterLine.TryResolve(out _, out _), Is.False, "No level from the source and not enabled: no line.");

            waterLine.Enabled = true;
            Assert.That(waterLine.TryResolve(out float level, out _), Is.True);
            Assert.That(level, Is.EqualTo(2f));

            source.Available = true;
            source.Level = float.NaN;
            Assert.That(waterLine.TryResolve(out level, out _), Is.True);
            Assert.That(level, Is.EqualTo(2f), "A non-finite source level is ignored.");
        }

        [Test]
        public void ThrowingSourceIsLoggedAndIgnored()
        {
            var waterLine = new GrassWaterLine { Enabled = true, Level = 1f, Source = new ThrowingSource() };
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: source failed");
            Assert.That(waterLine.TryResolve(out float level, out _), Is.True);
            Assert.That(level, Is.EqualTo(1f));
            // Logged once per source, not once per camera per frame.
            Assert.That(waterLine.TryResolve(out level, out _), Is.True);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void DestroyedUnityObjectSourceCountsAsNone()
        {
            var go = new GameObject("Grass Water Source Test");
            SourceBehaviour behaviour = go.AddComponent<SourceBehaviour>();
            var waterLine = new GrassWaterLine { Source = behaviour };
            Assert.That(waterLine.TryResolve(out float level, out _), Is.True);
            Assert.That(level, Is.EqualTo(4f));

            Object.DestroyImmediate(go);
            Assert.That(waterLine.IsActive, Is.False);
            Assert.That(waterLine.TryResolve(out _, out _), Is.False);
        }
    }
}
