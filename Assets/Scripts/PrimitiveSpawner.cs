using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ObjectSpawning
{
    // Stage 1: spawns primitives (shape/color/scale) at a fixed offset in front of the headset.
    // Manual keypress/controller-button trigger still spawns a default cube, for quick testing
    // without needing voice. Spawn(SpawnIntent) is the entry point the voice pipeline calls.
    // Stage 4: also the scene registry (tracks everything it has spawned by ID) and owns the
    // edit operations (resize/recolor/move/rotate/duplicate/delete) that act on that registry.
    public class PrimitiveSpawner : MonoBehaviour
    {
        [SerializeField] Transform headTransform;
        [SerializeField] float spawnDistance = 1.5f;

        // The actual floor/teleport-area object, so "on the ground" can be anchored to its real
        // world-space top surface instead of an assumed Y=0 -- the Teleport Area prefab's floor
        // mesh is a scaled default cube whose surface sits above its own transform's Y position,
        // which caused new objects to clip into the floor when Y=0 was hardcoded.
        [SerializeField] Transform floorReference;

        // A real project-asset material, not CreatePrimitive()'s implicit default. Build-time shader
        // variant stripping only preserves variants it can statically discover; a material that only
        // ever exists at runtime is invisible to that analysis, so its exact keyword combination
        // (e.g. stereo instancing) can get stripped even though the base shader itself survives.
        // Referencing this asset gives the stripper something concrete to see.
        [SerializeField] Material baseMaterial;

        // Stage 6: talks to the backend's Tripo3D-backed generation job. Optional -- if unwired,
        // SpawnGenerating still spawns a placeholder (never crashes), it just never gets replaced.
        [SerializeField] MeshGenerationClient meshGenerationClient;

        // Optional -- lets Spawn/Delete clear ObjectSelector's sticky pointed-at selection so a
        // stale pointer from before ("this table" from a minute ago) doesn't keep out-competing
        // the object the player just created or just lost. Without it wired, pointing still
        // overrides LastTouchedGameObject as before.
        [SerializeField] ObjectSelector objectSelector;

        // Stage 8: the scene's own directional/ambient light, adjusted by AdjustLighting --
        // optional, same "degrade gracefully if unwired" convention as meshGenerationClient.
        [SerializeField] Light directionalLight;

        const int MaxPointLights = 4;
        const float MinAmbientIntensity = 0.05f;
        const float MaxAmbientIntensity = 3f;

        static readonly Color GeneratingPlaceholderColor = new(0.6f, 0.6f, 0.6f);

        InputAction spawnAction;
        readonly Dictionary<int, GameObject> registry = new();
        int nextId;

        // Stage 9: what color/material an object currently wears, keyed by its registry id --
        // lets Recolor and Retexture undo each other correctly (reapplying a preset via
        // ApplyVisualState, not just a flat color) and lets RerollStyle know what to avoid
        // re-picking. Cleared alongside the registry on ClearAll.
        readonly struct VisualState
        {
            public readonly Color Color;
            public readonly MaterialPreset? Preset;
            public VisualState(Color color, MaterialPreset? preset)
            {
                Color = color;
                Preset = preset;
            }
        }
        readonly Dictionary<int, VisualState> visualState = new();

        // Stage 9: "undo that" -- a capped stack of inverse actions, one per mutating verb call.
        // Capped rather than unbounded so a long exhibition session (many small edits) can't grow
        // this forever; the cap also directly bounds how many Delete-hidden objects (see below)
        // can be waiting around at once. Revert closures never push their own new undo entry --
        // undoing an undo (redo) isn't part of this roadmap stage, so there's no risk of the stack
        // fighting itself.
        class UndoEntry
        {
            public string Description;
            public System.Action Revert;
            public System.Action OnEvicted;
        }
        readonly LinkedList<UndoEntry> undoStack = new();
        const int MaxUndoEntries = 8;

        // Stage 9: Delete no longer destroys immediately -- it deactivates and holds the object so
        // "undo that" can bring it back, same as every other edit. The object is only ever
        // permanently destroyed once its undo entry is evicted from the capped stack above (it can
        // no longer be reached) or the whole room is cleared -- never left to leak indefinitely.
        readonly HashSet<GameObject> hiddenForUndo = new();

        void PushUndo(string description, System.Action revert, System.Action onEvicted = null)
        {
            undoStack.AddLast(new UndoEntry { Description = description, Revert = revert, OnEvicted = onEvicted });
            if (undoStack.Count > MaxUndoEntries)
            {
                var evicted = undoStack.First.Value;
                undoStack.RemoveFirst();
                evicted.OnEvicted?.Invoke();
            }
        }

        // Reverts the single most recent mutating command, if any. Returns whether there was
        // anything to undo, so callers (voice/HUD) can tell a real revert apart from a no-op.
        public bool Undo()
        {
            if (undoStack.Count == 0)
            {
                Debug.Log("[PrimitiveSpawner] Nothing to undo.");
                return false;
            }

            var entry = undoStack.Last.Value;
            undoStack.RemoveLast();
            entry.Revert();
            Debug.Log($"[PrimitiveSpawner] Undid: {entry.Description}.");
            return true;
        }

        public int SpawnCount { get; private set; }
        public Vector3 LastSpawnPosition { get; private set; }

        // Stage 6: surfaced on DebugHud, mirrors VoiceCommandController's LastLlmError pattern --
        // generation failures happen asynchronously, well after the voice command that started
        // them, so they're tracked here rather than on any single command's result.
        public string LastGenerationError { get; private set; } = "";
        public int PendingGenerationCount { get; private set; }
        public int LastGenerationProgress { get; private set; }

        // Stage 4's default edit target when the player isn't actively pointing at anything --
        // "the last object I created or touched", per the roadmap's own stated simplification.
        public GameObject LastTouchedGameObject { get; private set; }

        void Awake()
        {
            spawnAction = new InputAction(name: "SpawnPrimitive", type: InputActionType.Button);
            spawnAction.AddBinding("<Keyboard>/space");
            spawnAction.AddBinding("<XRController>{RightHand}/triggerButton"); // right index trigger
            spawnAction.performed += OnSpawnPerformed;
        }

        void OnEnable() => spawnAction.Enable();

        void OnDisable() => spawnAction.Disable();

        void OnSpawnPerformed(InputAction.CallbackContext context) =>
            Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, 0.2f));

        static bool IsComposite(PrimitiveShape shape) => shape is PrimitiveShape.Table
            or PrimitiveShape.Shelf or PrimitiveShape.Crate or PrimitiveShape.Chair
            or PrimitiveShape.Stool or PrimitiveShape.Bench or PrimitiveShape.Sofa;

        public GameObject Spawn(SpawnIntent intent)
        {
            // Checked before any GameObject is created -- a clean, side-effect-free refusal
            // rather than building then discarding a light. Roadmap's own "keep it simple"
            // guidance for Stage 8 calls out exactly this cap ("a capped number of point lights
            // (e.g. 4) is plenty") given VR's unforgiving real-time dynamic light budget.
            if (intent.Shape == PrimitiveShape.PointLight && CountOfShape(PrimitiveShape.PointLight) >= MaxPointLights)
            {
                Debug.LogWarning($"[PrimitiveSpawner] Point light cap ({MaxPointLights}) reached -- " +
                    "delete one before adding another.");
                return null;
            }

            GameObject go;

            if (intent.Shape == PrimitiveShape.PointLight)
            {
                go = BuildPointLight(intent.Scale);
            }
            else if (IsComposite(intent.Shape))
            {
                go = ProceduralGeometryFactory.Build(intent.Shape);
                // Composite generators build at a fixed, real-world-proportioned canonical size
                // (a table is already table-sized), so small/medium/large is a modest relative
                // adjustment around that -- not an absolute size like it is for bare primitives.
                var scaleFactor = intent.Scale / VoiceIntentParser.DefaultScale;
                go.transform.localScale = Vector3.one * scaleFactor;
            }
            else
            {
                var primitiveType = intent.Shape switch
                {
                    PrimitiveShape.Sphere => PrimitiveType.Sphere,
                    PrimitiveShape.Cylinder => PrimitiveType.Cylinder,
                    _ => PrimitiveType.Cube,
                };

                go = GameObject.CreatePrimitive(primitiveType);
                go.transform.localScale = Vector3.one * intent.Scale;
            }

            go.name = $"Spawned{intent.Shape}_{SpawnCount}";

            var position = ResolveSpawnPosition(go, intent);
            go.transform.SetPositionAndRotation(position, Quaternion.identity);

            ApplyColor(go, intent.Color);
            var id = Register(go, intent.Shape);
            visualState[id] = new VisualState(intent.Color, null);

            SpawnCount++;
            LastSpawnPosition = position;
            LastTouchedGameObject = go;
            objectSelector?.ClearSelection();
            Debug.Log($"[PrimitiveSpawner] Spawned {intent.Shape} #{SpawnCount} at {position}");
            StartCoroutine(AnimateScaleIn(go.transform, go.transform.localScale));
            PushUndo($"spawn {go.name}", () => DestroyRegistered(go));
            return go;
        }

        // Stage 6: spawns an immediately-visible placeholder (a plain gray cube -- same
        // mechanism as a default create) and kicks off async generation in the background,
        // swapping the placeholder's visuals for the real mesh once it's ready. Never blocks;
        // failure/timeout just leaves the placeholder in place with LastGenerationError set.
        public GameObject SpawnGenerating(string prompt)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.localScale = Vector3.one * VoiceIntentParser.DefaultScale;
            go.name = $"Generating_{SpawnCount}";

            var position = ComputeOnGroundPosition(go, GetSpawnPosition(), GetFloorY());
            go.transform.SetPositionAndRotation(position, Quaternion.identity);

            ApplyColor(go, GeneratingPlaceholderColor);
            var id = Register(go, PrimitiveShape.Generated);
            visualState[id] = new VisualState(GeneratingPlaceholderColor, null);

            SpawnCount++;
            LastSpawnPosition = position;
            LastTouchedGameObject = go;
            objectSelector?.ClearSelection();
            Debug.Log($"[PrimitiveSpawner] Generating placeholder spawned for prompt=\"{prompt}\".");
            StartCoroutine(AnimateScaleIn(go.transform, go.transform.localScale));
            PushUndo($"spawn {go.name}", () => DestroyRegistered(go));

            if (meshGenerationClient != null)
            {
                PendingGenerationCount++;
                StartCoroutine(RunGeneration(go, prompt));
            }
            else
            {
                Debug.LogWarning("[PrimitiveSpawner] No MeshGenerationClient wired -- " +
                    "placeholder will never be replaced.");
            }

            return go;
        }

        IEnumerator RunGeneration(GameObject placeholder, string prompt)
        {
            string glbUrl = null;
            string generationError = null;
            var requestDone = false;

            meshGenerationClient.GenerateMesh(prompt, (url, err) =>
            {
                glbUrl = url;
                generationError = err;
                requestDone = true;
            }, progress => LastGenerationProgress = progress);

            yield return new WaitUntil(() => requestDone);

            // The placeholder (or the whole rig) may have been deleted/torn down while this was
            // in flight -- generation can take minutes. Never touch a stale/destroyed reference.
            if (placeholder == null)
            {
                Debug.Log($"[PrimitiveSpawner] Generation for prompt=\"{prompt}\" finished after " +
                    "its placeholder was deleted -- discarding result.");
                PendingGenerationCount--;
                yield break;
            }

            if (generationError != null)
            {
                LastGenerationError = generationError;
                Debug.LogWarning($"[PrimitiveSpawner] Generation failed for prompt=\"{prompt}\": {generationError}");
                PendingGenerationCount--;
                yield break;
            }

            yield return GeneratedMeshImporter.DownloadAndImport(glbUrl, placeholder.transform, (importedRoot, importError) =>
            {
                if (placeholder == null)
                {
                    Debug.Log($"[PrimitiveSpawner] Generated mesh for prompt=\"{prompt}\" finished after " +
                        "its placeholder was deleted -- discarding result.");
                    if (importedRoot != null)
                        DestroyObject(importedRoot);
                    return;
                }

                if (importError != null)
                {
                    LastGenerationError = importError;
                    Debug.LogWarning($"[PrimitiveSpawner] {importError}");
                    return;
                }

                SwapPlaceholderVisuals(placeholder, importedRoot);
            });

            PendingGenerationCount--;
        }

        // Placeholder was a plain CreatePrimitive(Cube) -- strip its primitive rendering/collision
        // now that the real generated mesh (already parented under it by the importer) is ready,
        // but keep the placeholder GameObject itself so SpawnedObjectInfo/registry id and any
        // outstanding "last touched"/pointed-at references to it keep working unchanged.
        void SwapPlaceholderVisuals(GameObject placeholder, GameObject importedRoot)
        {
            // Validate the imported mesh BEFORE touching the placeholder's own visuals -- an
            // untrusted external GLB that comes back with no renderers (corrupt download,
            // unexpected content) must never leave the object with neither the old cube nor a
            // working replacement.
            var renderers = importedRoot.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                LastGenerationError = "Generated mesh has no visible geometry -- keeping placeholder.";
                Debug.LogWarning($"[PrimitiveSpawner] {LastGenerationError}");
                DestroyObject(importedRoot);
                return;
            }

            var bounds = ComputeWorldBounds(importedRoot);

            // Only now that the replacement is confirmed valid, remove the placeholder cube's
            // own rendering/collision -- keeps the placeholder visible if anything above failed.
            if (placeholder.TryGetComponent<MeshFilter>(out var meshFilter))
                DestroyObject(meshFilter);
            if (placeholder.TryGetComponent<MeshRenderer>(out var meshRenderer))
                DestroyObject(meshRenderer);
            if (placeholder.TryGetComponent<Collider>(out var oldCollider))
                DestroyObject(oldCollider);

            // ObjectSelector's raycast needs *some* collider on the object -- size a fresh box
            // collider to the imported mesh's actual bounds now that it's in its final position.
            var box = placeholder.AddComponent<BoxCollider>();
            box.center = placeholder.transform.InverseTransformPoint(bounds.center);
            box.size = new Vector3(
                bounds.size.x / Mathf.Max(placeholder.transform.lossyScale.x, 0.0001f),
                bounds.size.y / Mathf.Max(placeholder.transform.lossyScale.y, 0.0001f),
                bounds.size.z / Mathf.Max(placeholder.transform.lossyScale.z, 0.0001f));

            LastTouchedGameObject = placeholder;
            Debug.Log($"[PrimitiveSpawner] Swapped generated mesh onto {placeholder.name}.");

            // Collider above is already sized to the mesh's true final bounds, so selection/
            // interaction works immediately -- only the visual scale animates in.
            StartCoroutine(AnimateScaleIn(importedRoot.transform, importedRoot.transform.localScale));
        }

        // Scales an object up from nothing over a short, cheap ease-out -- a single Vector3 lerp
        // per frame for a fraction of a second, negligible cost -- so new objects visibly appear
        // rather than blink into existence. Bails cleanly if the object is destroyed mid-animation
        // (e.g. a quick delete right after spawning).
        static IEnumerator AnimateScaleIn(Transform t, Vector3 targetScale, float duration = 0.25f)
        {
            if (t == null)
                yield break;

            t.localScale = Vector3.zero;
            var elapsed = 0f;
            while (elapsed < duration)
            {
                if (t == null)
                    yield break;
                elapsed += Time.deltaTime;
                var eased = 1f - Mathf.Pow(1f - Mathf.Clamp01(elapsed / duration), 3f);
                t.localScale = targetScale * eased;
                yield return null;
            }
            if (t != null)
                t.localScale = targetScale;
        }

        int Register(GameObject go, PrimitiveShape shape)
        {
            var info = go.AddComponent<SpawnedObjectInfo>();
            info.Id = nextId++;
            info.Shape = shape;
            registry[info.Id] = go;
            return info.Id;
        }

        // Reverts a Spawn/Duplicate: unlike public Delete, this permanently destroys the object
        // right away rather than hiding it for further undo -- undoing an undo (redo) isn't part
        // of this stage, so there's nothing left that would ever need it back.
        void DestroyRegistered(GameObject go)
        {
            if (go == null)
                return;

            var info = go.GetComponent<SpawnedObjectInfo>();
            if (info != null)
            {
                registry.Remove(info.Id);
                visualState.Remove(info.Id);
            }

            if (go == LastTouchedGameObject)
                LastTouchedGameObject = FindMostRecentlySpawned();
            objectSelector?.ClearSelection();

            Debug.Log($"[PrimitiveSpawner] Undo removed {go.name}.");
            DestroyObject(go);
        }

        // Stage 8: a small visible bulb (so it can actually be seen and pointed at -- an
        // invisible light source would be unselectable, since ObjectSelector's raycast needs a
        // collider, which CreatePrimitive(Sphere) already provides for free) plus a real Light
        // component. scale follows the same small/medium/large convention as every other create,
        // but drives range/intensity (the light's actual physical effect) rather than just the
        // bulb's visual size, since transform scale alone has no effect on a Light component.
        static GameObject BuildPointLight(float scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.localScale = Vector3.one * Mathf.Max(0.05f, scale * 0.3f);

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            var sizeRatio = scale / VoiceIntentParser.DefaultScale;
            light.range = 3f * sizeRatio;
            light.intensity = 2f * sizeRatio;

            return go;
        }

        int CountOfShape(PrimitiveShape shape)
        {
            var count = 0;
            foreach (var kv in registry)
            {
                if (kv.Value == null)
                    continue;
                var info = kv.Value.GetComponent<SpawnedObjectInfo>();
                if (info != null && info.Shape == shape)
                    count++;
            }
            return count;
        }

        // Stage 5: resolves "on"/"next to"/"on ground" relations into a concrete world position
        // using bounding boxes, falling back to the normal default (in front of the user) when no
        // relation was specified or no matching reference object exists yet -- never guesses,
        // never throws. Shared by both Spawn (placing a brand-new object) and Move (repositioning
        // an existing one) -- "put a light on the table" and "move it onto the table" resolve the
        // same way once you have an object and a relation, whichever it came from.
        Vector3 ResolvePlacement(GameObject obj, SpatialRelation? relation, PrimitiveShape? referenceShape,
            GameObject excludeFromReference)
        {
            // No relation specified ("spawn a cube") now defaults to floor level rather than
            // floating at head height -- reuses the same on-ground math "put it on the ground"
            // already used, just applied unconditionally instead of only when asked for.
            if (!relation.HasValue)
                return ComputeOnGroundPosition(obj, GetSpawnPosition(), GetFloorY());

            if (relation.Value == SpatialRelation.OnGround)
                return ComputeOnGroundPosition(obj, GetSpawnPosition(), GetFloorY());

            if (!referenceShape.HasValue)
                return GetSpawnPosition();

            var reference = FindMostRecentOfShape(referenceShape.Value, excludeFromReference);
            if (reference == null)
            {
                Debug.LogWarning($"[PrimitiveSpawner] No existing {referenceShape.Value} found to place " +
                    $"{relation.Value} of -- falling back to default placement.");
                return GetSpawnPosition();
            }

            return relation.Value switch
            {
                SpatialRelation.On => ComputeOnTopPosition(obj, reference),
                SpatialRelation.NextTo => ComputeNextToPosition(obj, reference),
                _ => GetSpawnPosition(),
            };
        }

        Vector3 ResolveSpawnPosition(GameObject newObject, SpawnIntent intent) =>
            ResolvePlacement(newObject, intent.Relation, intent.ReferenceShape, excludeFromReference: null);

        float? cachedFloorY;

        // Computed once from the real floor object's world bounds rather than assumed -- the
        // Teleport Area's floor mesh is a scaled cube whose top surface doesn't sit at its own
        // transform's Y position, so a hardcoded constant silently drifted out of sync with it.
        float GetFloorY()
        {
            if (!cachedFloorY.HasValue)
                cachedFloorY = floorReference != null ? ComputeWorldBounds(floorReference.gameObject).max.y : 0f;
            return cachedFloorY.Value;
        }

        // Most-recently-created object of the given shape -- mirrors Stage 4's "last touched"
        // simplification rather than attempting per-instance disambiguation ("the table" when
        // several exist just means the newest one). Excludes a given object (the Move target
        // itself) so an object never resolves as its own reference.
        GameObject FindMostRecentOfShape(PrimitiveShape shape, GameObject exclude)
        {
            GameObject best = null;
            var bestId = -1;
            foreach (var kv in registry)
            {
                if (kv.Value == null || kv.Value == exclude)
                    continue;
                var info = kv.Value.GetComponent<SpawnedObjectInfo>();
                if (info != null && info.Shape == shape && kv.Key > bestId)
                {
                    best = kv.Value;
                    bestId = kv.Key;
                }
            }
            return best;
        }

        static Bounds ComputeWorldBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        // Aligns the new object's bounds so it rests directly on top of the reference's bounds,
        // centered in X/Z -- computed from world-space bounds rather than transform position, so
        // it works regardless of each shape's own pivot convention (primitives are center-pivoted,
        // composites from ProceduralGeometryFactory are floor-pivoted).
        static Vector3 ComputeOnTopPosition(GameObject newObject, GameObject reference)
        {
            var refBounds = ComputeWorldBounds(reference);
            var newBounds = ComputeWorldBounds(newObject);
            var currentPos = newObject.transform.position;

            return currentPos + new Vector3(
                refBounds.center.x - newBounds.center.x,
                refBounds.max.y - newBounds.min.y,
                refBounds.center.z - newBounds.center.z);
        }

        // Reuses the normal look-direction X/Z (same as the default in-front-of-user placement),
        // but re-anchors Y so the object's bottom rests on the floor instead of at head height.
        static Vector3 ComputeOnGroundPosition(GameObject newObject, Vector3 lookDirectionPosition, float floorY)
        {
            var newBounds = ComputeWorldBounds(newObject);
            var deltaY = floorY - newBounds.min.y;
            var currentPos = newObject.transform.position;

            return new Vector3(lookDirectionPosition.x, currentPos.y + deltaY, lookDirectionPosition.z);
        }

        // Places the new object beside the reference (along the reference's own right vector, at
        // floor level), sized off both objects' bounds so they don't overlap. World-axis-aligned
        // bounds are an approximation once a reference has been rotated -- acceptable per the
        // roadmap's own "keep it simple" guidance for this stage.
        static Vector3 ComputeNextToPosition(GameObject newObject, GameObject reference)
        {
            const float margin = 0.08f;
            var refBounds = ComputeWorldBounds(reference);
            var newBounds = ComputeWorldBounds(newObject);
            var currentPos = newObject.transform.position;

            var direction = reference.transform.right;
            var targetCenter = refBounds.center + direction * (refBounds.extents.x + newBounds.extents.x + margin);

            return currentPos + new Vector3(
                targetCenter.x - newBounds.center.x,
                refBounds.min.y - newBounds.min.y,
                targetCenter.z - newBounds.center.z);
        }

        // multiplier is an explicit factor ("make it 10x bigger" -> multiplier=10, "make it 2x
        // smaller" -> multiplier=2), applied as a straight multiply when bigger and a divide when
        // not -- matching how people actually say "N times smaller" (half size, not 1/N smaller).
        // Null keeps the old fixed single-press 1.25x/0.8x step.
        //
        // Stage 8: when the target carries a Light component (a spawned PointLight), "bigger"/
        // "smaller" reinterprets as brighter/dimmer -- intensity and range -- instead of transform
        // scale, since scaling a light's GameObject has no effect on the light it casts.
        public void Resize(GameObject target, bool bigger, float? multiplier = null)
        {
            if (target == null)
                return;

            var factor = multiplier.HasValue
                ? (bigger ? multiplier.Value : 1f / multiplier.Value)
                : (bigger ? 1.25f : 0.8f);

            if (target.TryGetComponent<Light>(out var light))
            {
                var prevIntensity = light.intensity;
                var prevRange = light.range;
                PushUndo($"resize {target.name}", () =>
                {
                    if (light == null)
                        return;
                    light.intensity = prevIntensity;
                    light.range = prevRange;
                });

                light.intensity = Mathf.Clamp(light.intensity * factor, 0.05f, 12f);
                light.range = Mathf.Clamp(light.range * factor, 0.3f, 15f);
            }
            else
            {
                var prevScale = target.transform.localScale;
                PushUndo($"resize {target.name}", () =>
                {
                    if (target != null)
                        target.transform.localScale = prevScale;
                });

                target.transform.localScale *= factor;
            }

            LastTouchedGameObject = target;
            Debug.Log($"[PrimitiveSpawner] Resized {target.name} ({(bigger ? "bigger" : "smaller")}" +
                $"{(multiplier.HasValue ? $", {multiplier.Value:0.##}x" : "")}).");
        }

        public void Recolor(GameObject target, Color color)
        {
            if (target == null)
                return;

            var prevState = PriorVisualState(target);
            PushUndo($"recolor {target.name}", () => ApplyVisualState(target, prevState));
            ApplyRecolorCore(target, color);
        }

        void ApplyRecolorCore(GameObject target, Color color)
        {
            ApplyColor(target, color);
            var info = target.GetComponent<SpawnedObjectInfo>();
            if (info != null)
                visualState[info.Id] = new VisualState(color, null);
            LastTouchedGameObject = target;
            Debug.Log($"[PrimitiveSpawner] Recolored {target.name}.");
        }

        VisualState PriorVisualState(GameObject target)
        {
            var info = target.GetComponent<SpawnedObjectInfo>();
            return info != null && visualState.TryGetValue(info.Id, out var vs) ? vs : new VisualState(Color.white, null);
        }

        // Stage 9: shared revert target for both Recolor's and Retexture's undo entries -- a prior
        // state that was a flat color reapplies via Recolor's own logic, one that was a curated
        // preset reapplies via Retexture's, so undoing either one always lands back on whichever
        // it actually was before, not just a flat color guess.
        void ApplyVisualState(GameObject target, VisualState state)
        {
            if (target == null)
                return;

            if (state.Preset.HasValue)
                ApplyRetextureCore(target, state.Preset.Value);
            else
                ApplyRecolorCore(target, state.Color);
        }

        // Stage 7: applies a curated PBR preset (base color + metallic + smoothness, plus an
        // optional procedurally-generated detail texture -- see ProceduralTextureFactory) to
        // every renderer on the target -- works the same way whether the target is a plain
        // primitive, a ProceduralGeometryFactory composite, or a Stage 6 generated mesh. For a
        // generated mesh this deliberately replaces its downloaded texture with the preset,
        // mirroring the roadmap's "assign from a curated library" alternative to full generative
        // retexturing.
        public void Retexture(GameObject target, MaterialPreset preset)
        {
            if (target == null)
                return;

            var prevState = PriorVisualState(target);
            PushUndo($"retexture {target.name}", () => ApplyVisualState(target, prevState));
            ApplyRetextureCore(target, preset);
        }

        // Stage 9: picks a different curated preset than whatever the target currently wears
        // (falls back to any preset if it isn't currently retextured at all) and applies it through
        // the normal Retexture path above, so it's undoable exactly like a spoken retexture command
        // would be -- "try a different style" is just a retexture whose material Unity picks
        // instead of the user naming one.
        public void RerollStyle(GameObject target)
        {
            if (target == null)
                return;

            var current = PriorVisualState(target).Preset;
            var candidates = MaterialNaming.All;
            if (candidates.Count == 0)
                return;

            var next = candidates[Random.Range(0, candidates.Count)];
            if (candidates.Count > 1 && current.HasValue)
            {
                var attempts = 0;
                while (next.Name == current.Value.Name && attempts < 10)
                {
                    next = candidates[Random.Range(0, candidates.Count)];
                    attempts++;
                }
            }

            Retexture(target, next);
        }

        void ApplyRetextureCore(GameObject target, MaterialPreset preset)
        {
            // Some presets (wood, marble, stone, ...) get a small procedurally-generated detail
            // texture on top of the flat PBR parameters -- see ProceduralTextureFactory for which
            // ones and why. Generated once per material name, not per call.
            var detailTexture = ProceduralTextureFactory.GetOrCreate(preset);

            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
            {
                var instance = baseMaterial != null ? Instantiate(baseMaterial) : new Material(renderer.sharedMaterial);
                if (instance.HasProperty("_Metallic"))
                    instance.SetFloat("_Metallic", preset.Metallic);
                if (instance.HasProperty("_Smoothness"))
                    instance.SetFloat("_Smoothness", preset.Smoothness);

                if (detailTexture != null && instance.HasProperty("_BaseMap"))
                {
                    // The generated texture already bakes in preset.BaseColor with per-pixel
                    // variation -- URP multiplies _BaseMap by the _BaseColor tint, so leaving the
                    // tint at preset.BaseColor here would double-apply it (darker, oversaturated).
                    instance.SetTexture("_BaseMap", detailTexture);
                    instance.SetTextureScale("_BaseMap", ComputeDetailTiling(renderer));
                    instance.color = Color.white;
                }
                else
                {
                    instance.color = preset.BaseColor;
                }

                renderer.material = instance;
            }

            var info = target.GetComponent<SpawnedObjectInfo>();
            if (info != null)
                visualState[info.Id] = new VisualState(preset.BaseColor, preset);
            LastTouchedGameObject = target;
            Debug.Log($"[PrimitiveSpawner] Retextured {target.name} as {preset.Name}.");
        }

        // Roughly matches the detail texture's apparent "print size" across very differently
        // sized parts (a thin chair leg vs. a tabletop) -- a fixed tile count either smears into
        // an illegibly huge blob on large surfaces or looks like noise on tiny ones. The
        // generated texture is exactly seamless (see ProceduralTextureFactory), so any repeat
        // count blends cleanly with no visible seam. This can't correct anisotropic stretch on a
        // single non-uniformly-scaled part, though (a table leg's tall thin side faces still
        // stretch the pattern along their long axis) -- a single _BaseMap tiling value applies to
        // the whole mesh's UV set, not per-face, and fixing that fully would need triplanar
        // (world-space) texture projection instead of relying on primitive UVs at all.
        static Vector2 ComputeDetailTiling(Renderer renderer)
        {
            const float metersPerTile = 0.3f;
            var size = renderer.bounds.size;
            var largest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            var tiles = Mathf.Max(1f, Mathf.Round(largest / metersPerTile));
            return new Vector2(tiles, tiles);
        }

        // Stage "advanced instructions": distanceMeters+direction ("move it 3 meters to the
        // left") is an alternative to relation-based placement, not a combination -- when both
        // are given, the distance/direction move wins. With neither, relation/referenceShape
        // unset ("move it here") just moves to the default in-front-of-user spot, same as before
        // Stage 5 added relations. With relation set ("move it onto the table", "move it to the
        // ground"), reuses the exact same placement math Spawn uses for create.
        public void Move(GameObject target, SpatialRelation? relation = null, PrimitiveShape? referenceShape = null,
            float? distanceMeters = null, MoveDirection? direction = null)
        {
            if (target == null)
                return;

            var prevPosition = target.transform.position;
            PushUndo($"move {target.name}", () =>
            {
                if (target != null)
                    target.transform.position = prevPosition;
            });

            if (distanceMeters.HasValue && direction.HasValue)
            {
                target.transform.position += ComputeDirectionalOffset(direction.Value, distanceMeters.Value);
                LastTouchedGameObject = target;
                Debug.Log($"[PrimitiveSpawner] Moved {target.name} {distanceMeters.Value:0.##}m {direction.Value}.");
                return;
            }

            target.transform.position = ResolvePlacement(target, relation, referenceShape, excludeFromReference: target);
            LastTouchedGameObject = target;
            Debug.Log($"[PrimitiveSpawner] Moved {target.name}.");
        }

        // Left/right/forward/backward are relative to the PLAYER's current flattened facing
        // direction (matching how the default spawn position already resolves), not the target
        // object's own orientation -- "move it to the left" only means something intuitive
        // relative to whoever's giving the command. Up/down are plain world-space.
        Vector3 ComputeDirectionalOffset(MoveDirection direction, float meters)
        {
            var flatForward = headTransform != null
                ? Vector3.ProjectOnPlane(headTransform.forward, Vector3.up)
                : Vector3.forward;
            if (flatForward.sqrMagnitude < 0.001f)
                flatForward = Vector3.forward;
            flatForward.Normalize();
            var flatRight = Vector3.Cross(Vector3.up, flatForward);

            return direction switch
            {
                MoveDirection.Forward => flatForward * meters,
                MoveDirection.Backward => -flatForward * meters,
                MoveDirection.Right => flatRight * meters,
                MoveDirection.Left => -flatRight * meters,
                MoveDirection.Up => Vector3.up * meters,
                MoveDirection.Down => Vector3.down * meters,
                _ => Vector3.zero,
            };
        }

        // Repositions target's Y so its bottom rests on the floor, keeping whatever X/Z it's
        // currently at -- reuses the exact same floor math every other placement path already
        // uses (ComputeOnGroundPosition/GetFloorY), just anchored to the object's own current
        // position instead of a fresh spawn position. For the ray-grab feature: dropping a held
        // object should always land it solidly on the floor, never mid-air or clipped into it,
        // regardless of where along the beam it was released.
        public void SnapToGround(GameObject target)
        {
            if (target == null)
                return;

            target.transform.position = ComputeOnGroundPosition(target, target.transform.position, GetFloorY());
        }

        // Marks target as the active edit target without moving/resizing/recoloring it -- for
        // interactions (like ray-grabbing) that already changed the object some other way and
        // just need voice commands ("make it bigger") to target it afterward, the same way
        // Spawn/Delete already keep LastTouchedGameObject in sync with whatever the player last
        // interacted with.
        public void MarkAsTouched(GameObject target)
        {
            if (target == null)
                return;

            LastTouchedGameObject = target;
            objectSelector?.ClearSelection();
        }

        // degrees defaults to a single fixed-size turn (the old always-90-degrees behavior) when
        // the caller has no specific amount to give ("rotate it" with no number). A specific
        // amount ("rotate it 180 degrees") overrides that entirely, sign and all.
        public void Rotate(GameObject target, float degrees = 90f)
        {
            if (target == null)
                return;

            var prevRotation = target.transform.rotation;
            PushUndo($"rotate {target.name}", () =>
            {
                if (target != null)
                    target.transform.rotation = prevRotation;
            });

            target.transform.Rotate(Vector3.up, degrees, Space.World);
            LastTouchedGameObject = target;
            Debug.Log($"[PrimitiveSpawner] Rotated {target.name} by {degrees:0.##} degrees.");
        }

        public GameObject Duplicate(GameObject target)
        {
            if (target == null)
                return null;

            // Capture color before cloning and re-apply fresh material instances afterward,
            // rather than trusting Instantiate() to correctly split ownership of the source's
            // already-instanced materials -- avoids the original and the copy silently sharing
            // a material object (and so changing color together) later.
            var sourceRenderer = target.GetComponentInChildren<Renderer>();
            var color = sourceRenderer != null ? sourceRenderer.material.color : Color.white;
            var shape = target.GetComponent<SpawnedObjectInfo>()?.Shape ?? PrimitiveShape.Cube;
            var sourceVisual = PriorVisualState(target);

            var copy = Instantiate(target, GetSpawnPosition(), target.transform.rotation);
            copy.name = $"{target.name}_Copy";

            var oldInfo = copy.GetComponent<SpawnedObjectInfo>();
            if (oldInfo != null)
                DestroyObject(oldInfo);
            var copyId = Register(copy, shape);
            visualState[copyId] = sourceVisual;
            ApplyColor(copy, color);

            SpawnCount++;
            LastTouchedGameObject = copy;
            Debug.Log($"[PrimitiveSpawner] Duplicated {target.name} -> {copy.name}.");
            PushUndo($"duplicate {target.name}", () => DestroyRegistered(copy));
            return copy;
        }

        // Stage 9: hides rather than destroys, so "undo that" can bring it straight back --
        // permanently destroyed only once its undo entry is evicted (see PushUndo) or the whole
        // room is cleared. A hidden object costs nothing in the meantime; Unity doesn't render or
        // simulate an inactive GameObject.
        public void Delete(GameObject target)
        {
            if (target == null)
                return;

            var info = target.GetComponent<SpawnedObjectInfo>();
            var id = info != null ? info.Id : -1;
            if (info != null)
                registry.Remove(info.Id);

            // Delete always acts on whatever is currently resolved as the edit target (there's
            // no other way to reach this method), so the thing just deleted was, by definition,
            // the edit target -- fall back to whatever's now the most recently spawned survivor
            // rather than leaving the player with nothing selected. Also clears the pointer's own
            // sticky selection: Unity's overridden equality already treats a destroyed reference
            // as null, so GetPointedAtObject() would fall through on its own regardless, but
            // clearing it explicitly doesn't rely on that being true everywhere it's read.
            LastTouchedGameObject = FindMostRecentlySpawned();
            objectSelector?.ClearSelection();

            Debug.Log($"[PrimitiveSpawner] Deleted {target.name}.");
            target.SetActive(false);
            hiddenForUndo.Add(target);

            PushUndo($"delete {target.name}",
                revert: () =>
                {
                    if (target == null)
                        return;
                    hiddenForUndo.Remove(target);
                    target.SetActive(true);
                    if (info != null)
                        registry[id] = target;
                    LastTouchedGameObject = target;
                    objectSelector?.ClearSelection();
                    Debug.Log($"[PrimitiveSpawner] Restored {target.name}.");
                },
                onEvicted: () =>
                {
                    hiddenForUndo.Remove(target);
                    if (target != null)
                        DestroyObject(target);
                });
        }

        // Highest registry id still present (registry is keyed by nextId, which only ever
        // increases) -- "the most recently spawned object that still exists."
        GameObject FindMostRecentlySpawned()
        {
            GameObject best = null;
            var bestId = -1;
            foreach (var kv in registry)
            {
                if (kv.Value != null && kv.Key > bestId)
                {
                    best = kv.Value;
                    bestId = kv.Key;
                }
            }
            return best;
        }

        // Exhibition-facing bulk reset ("clear the room" / "reset the room"), so staff can wipe
        // the scene between visitors instead of deleting objects one at a time. Any placeholder
        // destroyed here mid-generation is discarded harmlessly by RunGeneration's own
        // "placeholder == null" check once that request eventually completes.
        public void ClearAll()
        {
            var targets = new List<GameObject>(registry.Values);
            var count = 0;
            foreach (var go in targets)
            {
                if (go == null)
                    continue;
                DestroyObject(go);
                count++;
            }

            registry.Clear();

            // Also permanently destroys anything Delete was only hiding for possible undo, and
            // drops the undo stack itself -- reverting to a state from before a full room reset
            // wouldn't mean anything.
            foreach (var hidden in hiddenForUndo)
            {
                if (hidden != null)
                    DestroyObject(hidden);
            }
            hiddenForUndo.Clear();
            undoStack.Clear();
            visualState.Clear();

            LastTouchedGameObject = null;
            SpawnCount = 0;
            LastGenerationError = "";

            Debug.Log($"[PrimitiveSpawner] Cleared {count} object(s).");
        }

        // Exposed for the exhibition scene's spawn-preview marker: X/Z from the current look
        // direction, snapped straight to floor height for display -- a flat floor marker doesn't
        // need ComputeOnGroundPosition's per-object pivot correction, it just needs to sit on the
        // floor at the spot a new object's footprint would center on.
        public Vector3 GetSpawnPreviewPosition()
        {
            var raw = GetSpawnPosition();
            return new Vector3(raw.x, GetFloorY(), raw.z);
        }

        // Destroy() only works in Play Mode; Edit Mode (including tests) requires DestroyImmediate.
        static void DestroyObject(Object obj)
        {
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        void ApplyColor(GameObject go, Color color)
        {
            // A PointLight's own bulb sphere sits essentially at its Light's origin, so the
            // intense nearby illumination blows the bulb's own surface out to white under normal
            // lit shading regardless of base color -- confirmed in headset testing ("the light
            // object releases a glow but the object itself doesn't [tint], it has a white
            // texture"). Emission bypasses that: it's an additive glow term, not a lit response,
            // so the bulb reads as the requested color instead of just contributing a correctly-
            // tinted glow to everything else nearby while staying white itself.
            var isLight = go.TryGetComponent<Light>(out var light);

            foreach (var renderer in go.GetComponentsInChildren<Renderer>())
            {
                Material instance;
                if (baseMaterial != null)
                {
                    instance = Instantiate(baseMaterial);
                    instance.color = color;
                    renderer.material = instance;
                }
                else
                {
                    instance = renderer.material;
                    instance.color = color;
                }

                if (isLight && instance.HasProperty("_EmissionColor"))
                {
                    instance.EnableKeyword("_EMISSION");
                    instance.SetColor("_EmissionColor", color);
                }
            }

            // Tinting the light's own color too means create/recolor change what the light
            // actually casts, not just how the bulb itself looks.
            if (isLight)
                light.color = color;
        }

        // Stage 8: the scene's directional/ambient light -- unlike Resize/Recolor there's no
        // registry target to resolve here (mirrors ClearAll's own "no target" dispatch in
        // VoiceCommandController.ApplyEdit). brighter/multiplier/color are each independently
        // optional so a pure color command ("make the lighting warm") never silently resets
        // brightness, and vice versa.
        public void AdjustLighting(bool? brighter, float? multiplier, Color? color)
        {
            if (directionalLight == null)
            {
                Debug.LogWarning("[PrimitiveSpawner] No directional light wired -- cannot adjust scene lighting.");
                return;
            }

            var light = directionalLight;
            var prevIntensity = light.intensity;
            var prevColor = light.color;
            PushUndo("adjust scene lighting", () =>
            {
                if (light == null)
                    return;
                light.intensity = prevIntensity;
                light.color = prevColor;
            });

            if (brighter.HasValue)
            {
                var factor = multiplier.HasValue
                    ? (brighter.Value ? multiplier.Value : 1f / multiplier.Value)
                    : (brighter.Value ? 1.25f : 0.8f);
                directionalLight.intensity = Mathf.Clamp(directionalLight.intensity * factor, MinAmbientIntensity, MaxAmbientIntensity);
            }

            if (color.HasValue)
                directionalLight.color = color.Value;

            Debug.Log($"[PrimitiveSpawner] Adjusted scene lighting " +
                $"(brighter={(brighter.HasValue ? brighter.Value.ToString() : "unchanged")}, " +
                $"color={(color.HasValue ? color.Value.ToString() : "unchanged")}).");
        }

        Vector3 GetSpawnPosition()
        {
            if (headTransform == null)
                return transform.position + Vector3.forward * spawnDistance;

            var flatForward = Vector3.ProjectOnPlane(headTransform.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 0.001f)
                flatForward = Vector3.forward;
            flatForward.Normalize();

            return headTransform.position + flatForward * spawnDistance;
        }
    }
}
