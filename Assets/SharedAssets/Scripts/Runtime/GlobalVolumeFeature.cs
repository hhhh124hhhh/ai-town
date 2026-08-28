using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

//TODO: Take a look at this script and clean it up
public class GlobalVolumeFeature : ScriptableRendererFeature
{
    class GlobalVolumePass : ScriptableRenderPass
    {
        public VolumeProfile _baseProfile;
        public static Volume vol;
        public static GameObject volumeHolder;

        public void CreateVolumeIfNotExists() 
        {
            if(volumeHolder == null)
            {
                volumeHolder = new GameObject("[DefaultVolume]");
                vol = volumeHolder.AddComponent<Volume>();
                vol.isGlobal = true;
                volumeHolder.hideFlags = HideFlags.HideAndDontSave;
            }
        }
        
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            if (vol && _baseProfile)
            {
                vol.sharedProfile = _baseProfile;
            }
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            
        }

        public override void OnFinishCameraStackRendering(CommandBuffer cmd)
        {
            if (vol)
            {
                //vol.sharedProfile = null;    
            }
        }
    }

    GlobalVolumePass m_ScriptablePass;

    public VolumeProfile _baseProfile;

    /// <inheritdoc/>
    public override void Create()
    {
        if (GlobalVolumePass.vol)
        {
            GlobalVolumePass.vol.sharedProfile = null;
        }

        m_ScriptablePass = new GlobalVolumePass
        {
            renderPassEvent = RenderPassEvent.BeforeRendering,
            _baseProfile = this._baseProfile,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (GlobalVolumePass.volumeHolder == null)
        {
            var old = GameObject.Find("[DefaultVolume]");
            if (old != null)
            {
                if (Application.isPlaying)
                    Destroy(old);
                else
                    DestroyImmediate(old);
            }
        }
        m_ScriptablePass.CreateVolumeIfNotExists();
        renderer.EnqueuePass(m_ScriptablePass);
    }
    
    
}