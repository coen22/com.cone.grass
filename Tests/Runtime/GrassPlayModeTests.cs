using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cone.Grass.Tests
{
    /// <summary>
    /// Play Mode behaviour a game sees: the renderer's lifetime, a character bending grass with its collider while it
    /// walks across a terrain, and the bent grass recovering after it leaves.
    /// </summary>
    public sealed class GrassPlayModeTests
    {
        private GameObject _terrainObject, _grassObject, _character;
        private TerrainData _terrainData;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _terrainData = new TerrainData { heightmapResolution = 33 };
            _terrainData.size = new Vector3(32f, 4f, 32f);
            _terrainObject = Terrain.CreateTerrainGameObject(_terrainData);
            _terrainObject.name = "Grass test terrain";

            _grassObject = new GameObject("Grass test renderer");
            _grassObject.SetActive(false);
            _grassObject.AddComponent<InfiniteGrassRenderer>();
            var area = _grassObject.AddComponent<GrassPlacementArea>();
            area.ConfigureTexture(_terrainObject.GetComponent<Terrain>(), Texture2D.whiteTexture);
            _grassObject.SetActive(true);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Object.Destroy(_character);
            Object.Destroy(_grassObject);
            Object.Destroy(_terrainObject);
            yield return null;
            Object.Destroy(_terrainData);
        }

        [UnityTest]
        public IEnumerator RendererRegistersWhileEnabledAndReleasesWhenDisabled()
        {
            var renderer = _grassObject.GetComponent<InfiniteGrassRenderer>();
            Assert.That(InfiniteGrassRenderer.Instance, Is.SameAs(renderer));
            Assert.That(renderer.IsReadyForRendering, Is.True);

            renderer.enabled = false;
            yield return null;
            Assert.That(InfiniteGrassRenderer.Instance, Is.Null);

            renderer.enabled = true;
            yield return null;
            Assert.That(InfiniteGrassRenderer.Instance, Is.SameAs(renderer));
        }

        [UnityTest]
        public IEnumerator WalkingCharacterBendsGrassAndItRecovers()
        {
            _character = new GameObject("Grass test character");
            _character.transform.position = new Vector3(8f, 1.0f, 8f);
            var controller = _character.AddComponent<CharacterController>();
            controller.height = 1.6f;
            controller.radius = 0.3f;
            controller.center = new Vector3(0f, 0.8f, 0f);
            _character.transform.position = new Vector3(8f, 0.02f, 8f);
            var interactor = _character.AddComponent<GrassColliderInteractor>();
            interactor.Configure(controller);

            bool contact = false;
            for (int frame = 0; frame < 60; frame++)
            {
                controller.Move(new Vector3(0.05f, -0.01f, 0.03f));
                yield return null;
                contact |= interactor.State == GrassColliderInteractor.InteractionState.Active;
            }

            Assert.That(contact, Is.True, "The character never touched the grass: " + interactor.Diagnostic);
            Assert.That(interactor.ActiveNodeCount, Is.GreaterThan(0));

            // Lift the character clear of the grass; the trail recovers and its nodes are released.
            _character.transform.position += Vector3.up * 5f;
            float deadline = Time.time + 6f;
            while (interactor.ActiveNodeCount > 0 && Time.time < deadline)
            {
                yield return null;
            }

            Assert.That(interactor.ActiveNodeCount, Is.Zero, "The bent grass did not recover after the character left.");
        }

        [UnityTest]
        public IEnumerator DestroyingTheCharacterLeavesNoActiveInteractor()
        {
            _character = new GameObject("Grass test character");
            var controller = _character.AddComponent<CharacterController>();
            _character.AddComponent<GrassColliderInteractor>().Configure(controller);
            yield return null;
            Assert.That(GrassColliderInteractor.ActiveInteractors, Has.Count.EqualTo(1));

            Object.Destroy(_character);
            yield return null;
            Assert.That(GrassColliderInteractor.ActiveInteractors, Is.Empty);
        }
    }
}
