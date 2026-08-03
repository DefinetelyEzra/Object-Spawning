using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ObjectSpawning
{
    // Stage 6: talks to the backend's /generate-mesh and /generation-status endpoints, which
    // wrap Tripo3D's async text-to-3D task API. Generation takes anywhere from ~20s to a few
    // minutes, so this polls on a fixed interval rather than a single request/response like
    // /parse-intent, and gives up (treated as a failure, never a silent hang) past maxWaitSeconds.
    public class MeshGenerationClient : MonoBehaviour
    {
        [SerializeField] string backendUrl = "http://127.0.0.1:8000";
        [SerializeField] float pollIntervalSeconds = 3f;
        [SerializeField] float maxWaitSeconds = 180f;

        string EffectiveBackendUrl => BackendUrlResolver.Resolve(backendUrl);

        [Serializable]
        class GenerateMeshRequestBody
        {
            public string prompt;
        }

        [Serializable]
        class GenerateMeshResponseBody
        {
            public string job_id;
        }

        [Serializable]
        class GenerationStatusResponseBody
        {
            public string stage; // "pending" | "done" | "failed"
            public string final_glb_url;
            public string error;
        }

        // onComplete(glbUrl, error): on success, the downloadable GLB URL and null error. On
        // failure (request error, backend-reported failure, or timeout), a null URL and a
        // non-empty error describing why. Never throws -- callers can treat this as the single
        // place generation can go wrong.
        public void GenerateMesh(string prompt, Action<string, string> onComplete) =>
            StartCoroutine(GenerateMeshRoutine(prompt, onComplete));

        IEnumerator GenerateMeshRoutine(string prompt, Action<string, string> onComplete)
        {
            var bodyJson = JsonUtility.ToJson(new GenerateMeshRequestBody { prompt = prompt });
            var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);

            using var startRequest = new UnityWebRequest($"{EffectiveBackendUrl}/generate-mesh", "POST");
            startRequest.uploadHandler = new UploadHandlerRaw(bodyBytes);
            startRequest.downloadHandler = new DownloadHandlerBuffer();
            startRequest.SetRequestHeader("Content-Type", "application/json");
            startRequest.timeout = 15;

            yield return startRequest.SendWebRequest();

            if (startRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[MeshGenerationClient] Failed to start generation: {startRequest.error}");
                onComplete?.Invoke(null, $"Could not start generation: {startRequest.error}");
                yield break;
            }

            GenerateMeshResponseBody startResponse;
            try
            {
                startResponse = JsonUtility.FromJson<GenerateMeshResponseBody>(startRequest.downloadHandler.text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MeshGenerationClient] Malformed start response: {e.Message}");
                onComplete?.Invoke(null, $"Malformed response starting generation: {e.Message}");
                yield break;
            }

            if (startResponse == null || string.IsNullOrEmpty(startResponse.job_id))
            {
                onComplete?.Invoke(null, "Backend did not return a job id.");
                yield break;
            }

            Debug.Log($"[MeshGenerationClient] Started generation job {startResponse.job_id} for prompt=\"{prompt}\"");

            var elapsed = 0f;
            while (elapsed < maxWaitSeconds)
            {
                yield return new WaitForSeconds(pollIntervalSeconds);
                elapsed += pollIntervalSeconds;

                using var pollRequest = UnityWebRequest.Get($"{EffectiveBackendUrl}/generation-status/{startResponse.job_id}");
                pollRequest.timeout = 15;
                yield return pollRequest.SendWebRequest();

                if (pollRequest.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[MeshGenerationClient] Status poll failed: {pollRequest.error}");
                    onComplete?.Invoke(null, $"Status check failed: {pollRequest.error}");
                    yield break;
                }

                GenerationStatusResponseBody status;
                try
                {
                    status = JsonUtility.FromJson<GenerationStatusResponseBody>(pollRequest.downloadHandler.text);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MeshGenerationClient] Malformed status response: {e.Message}");
                    onComplete?.Invoke(null, $"Malformed status response: {e.Message}");
                    yield break;
                }

                if (status == null)
                {
                    onComplete?.Invoke(null, "Empty status response from backend.");
                    yield break;
                }

                if (status.stage == "done")
                {
                    if (string.IsNullOrEmpty(status.final_glb_url))
                    {
                        onComplete?.Invoke(null, "Generation reported done but returned no model URL.");
                        yield break;
                    }

                    Debug.Log($"[MeshGenerationClient] Job {startResponse.job_id} complete.");
                    onComplete?.Invoke(status.final_glb_url, null);
                    yield break;
                }

                if (status.stage == "failed")
                {
                    var error = string.IsNullOrEmpty(status.error) ? "Generation failed." : status.error;
                    Debug.LogWarning($"[MeshGenerationClient] Job {startResponse.job_id} failed: {error}");
                    onComplete?.Invoke(null, error);
                    yield break;
                }

                // else "pending" -- keep polling
            }

            Debug.LogWarning($"[MeshGenerationClient] Job {startResponse.job_id} timed out after {maxWaitSeconds}s.");
            onComplete?.Invoke(null, $"Generation timed out after {maxWaitSeconds:0}s.");
        }
    }
}
