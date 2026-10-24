using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NGGMultiplayer.Runtime
{
    /// <summary>
    /// Keeps a label facing the camera that draws it. Under URP the label is turned for each camera just before that
    /// camera renders, so a second camera tagged MainCamera (or one that is not the one on screen) can never leave the
    /// text facing away and mirrored. LateUpdate turns it toward the best screen camera as a fallback.
    /// </summary>
    internal sealed class Billboard : MonoBehaviour
    {
        private static readonly List<Billboard> Active = new List<Billboard>();
        private static bool _hooked;

        private Camera _camera;

        private void OnEnable()
        {
            Active.Add(this);
            if (!_hooked)
            {
                _hooked = true;
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            }
        }

        private void OnDisable() => Active.Remove(this);

        private void LateUpdate()
        {
            if (_camera == null || !_camera.isActiveAndEnabled || _camera.targetTexture != null)
            {
                _camera = ScreenCamera();
                if (_camera == null)
                    return;
            }
            Face(_camera);
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Game)
                return;
            for (int i = 0; i < Active.Count; i++)
            {
                if (Active[i] != null)
                    Active[i].Face(camera);
            }
        }

        // Forward points from the camera through the label: the side a TextMesh reads correctly from.
        private void Face(Camera camera)
        {
            var t = transform;
            var cam = camera.transform;
            var forward = t.position - cam.position;
            if (forward.sqrMagnitude < 1e-6f)
                forward = cam.forward;
            t.rotation = Quaternion.LookRotation(forward, cam.up);
        }

        /// <summary>The enabled camera drawing to the screen with the highest depth (what the player sees).</summary>
        private static Camera ScreenCamera()
        {
            Camera best = null;
            foreach (var camera in Camera.allCameras)
            {
                if (camera == null || !camera.isActiveAndEnabled || camera.targetTexture != null)
                    continue;
                if (best == null || camera.depth > best.depth)
                    best = camera;
            }
            return best ?? Camera.main;
        }
    }
}
