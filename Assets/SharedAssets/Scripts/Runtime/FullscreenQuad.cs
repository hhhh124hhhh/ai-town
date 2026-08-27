using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Scripting;

[ExecuteAlways, DefaultExecutionOrder(600)]
public class FullscreenQuad : MonoBehaviour
{
    private CameraFullscreenQuad _pass;

    public RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingTransparents;
    public int injectionPointOffset = 0;
    public Material material;
    public ScriptableRenderPassInput inputRequirements;
    
    private void OnEnable()
    {
        _pass = new CameraFullscreenQuad();
        
        // setup callback
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
    }

    private void OnBeginCamera(ScriptableRenderContext context, Camera cam)
    {
        //if (_pass == null) return;
        
        // injection point
        _pass.renderPassEvent = injectionPoint + injectionPointOffset;
        _pass.passMaterial = material;
        _pass.inputReq = inputRequirements;
        
        // inject pass
        cam.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(_pass);
    }

    internal class CameraFullscreenQuad : ScriptableRenderPass
    {
        public Material passMaterial;
        private LocalKeyword keyword;
        public ScriptableRenderPassInput inputReq = ScriptableRenderPassInput.None;

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            if (passMaterial != null)
            {
                keyword = new LocalKeyword(passMaterial.shader, "_FLIPY");
            }
            ConfigureInput(inputReq);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            //if (passMaterial == null) return;
            // do render
            
            Debug.Log("injecting pass");

            var cmd = CommandBufferPool.Get("CameraFullscreenQuad");
            
            var flipY = renderingData.cameraData.IsRenderTargetProjectionMatrixFlipped(renderingData.cameraData.renderer.cameraColorTargetHandle);
            passMaterial.SetKeyword(keyword, flipY);
            var cam = renderingData.cameraData.camera;
            passMaterial.SetMatrix("_InverseViewProjection", (GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix).inverse);
            CoreUtils.DrawFullScreen(cmd, passMaterial);
            
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
        }
    }
}


#if PARALLEL_RENDERING && ENABLE_PARALLEL_RENDERING_SCRIPT
public class FullscreenQuad_Proxy : MonoBehaviour_Proxy
{
    private FullscreenQuad.CameraFullscreenQuad _pass;

    [Preserve]
    public void OnProxyCreate(MonoBehaviour monoBehaviour)
    {
        if (monoBehaviour is FullscreenQuad fullscreenQuad)
        {
            _pass = GetOrCreateCameraFullscreenQuad();
            // injection point
            _pass.renderPassEvent = fullscreenQuad.injectionPoint + fullscreenQuad.injectionPointOffset;
            _pass.passMaterial = fullscreenQuad.material;
            _pass.inputReq = fullscreenQuad.inputRequirements;

            RenderPipelineManager_THR.beginCameraRenderingThr += OnBeginCameraThr;
        }
        else
        {
            Debug.LogError($"Failed to create FullscreenQuad_Proxy from {monoBehaviour.name}: type mismatch.");
        }
    }

    [Preserve]
    public void OnProxyDestroy()
    {
        ReleaseCameraFullscreenQuad(_pass);
        _pass = null;

        RenderPipelineManager_THR.beginCameraRenderingThr -= OnBeginCameraThr;
    }

    private void OnBeginCameraThr(ScriptableRenderContext context, Camera_THX_Proxy cam)
    {
        if (_pass == null) return;

        // inject pass
        cam.TryGetComponent<UniversalAdditionalCameraData_Proxy>(out var cameraData);
        cameraData.scriptableRenderer.EnqueuePass(_pass);
    }

    /// <summary>
    /// Pool for reusing CameraFullscreenQuad instances (max 32) to reduce GC allocation.
    /// </summary>
    private static Queue<FullscreenQuad.CameraFullscreenQuad> s_CachedCameraFullscreenQuad;
    private const int kMaxPoolSize = 32;

    private static FullscreenQuad.CameraFullscreenQuad GetOrCreateCameraFullscreenQuad()
    {
        if (s_CachedCameraFullscreenQuad != null && s_CachedCameraFullscreenQuad.Count > 0)
        {
            return s_CachedCameraFullscreenQuad.Dequeue();
        }
        return new FullscreenQuad.CameraFullscreenQuad();
    }

    private static void ReleaseCameraFullscreenQuad(FullscreenQuad.CameraFullscreenQuad fullscreenQuad)
    {
        if (fullscreenQuad != null)
        {
            s_CachedCameraFullscreenQuad ??= new Queue<FullscreenQuad.CameraFullscreenQuad>();
            if (s_CachedCameraFullscreenQuad.Count < kMaxPoolSize)
            {
                fullscreenQuad.passMaterial = null;
                s_CachedCameraFullscreenQuad.Enqueue(fullscreenQuad);
            }
            // Pool full; let GC reclaim the instance.
        }
    }
}
#endif