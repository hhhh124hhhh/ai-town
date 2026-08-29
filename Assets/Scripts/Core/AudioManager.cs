using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 全局音频管理：BGM 2D 循环 + SFX PlayOneShot。
/// 懒创建单例，clip 从 Resources/Audio/ 加载（BGM_Town / SFX_Build / SFX_Click / SFX_Bell / SFX_Bubble / SFX_Gong）。
/// clip 缺失时静默跳过，不影响游戏逻辑。
/// </summary>
public class AudioManager : MonoBehaviour
{
    private static AudioManager _i;
    private AudioSource _bgm;
    private AudioSource _amb;
    private AudioSource _sfx;
    private readonly Dictionary<string, AudioClip> _cache = new();

    public static AudioManager I
    {
        get
        {
            if (_i == null)
            {
                var go = new GameObject("AudioManager");
                _i = go.AddComponent<AudioManager>();
                DontDestroyOnLoad(go);
            }
            return _i;
        }
    }

    private void Awake()
    {
        _bgm = gameObject.AddComponent<AudioSource>();
        _bgm.loop = true;
        _bgm.playOnAwake = false;
        _bgm.spatialBlend = 0f;
        _bgm.volume = 0.32f;

        _sfx = gameObject.AddComponent<AudioSource>();
        _sfx.playOnAwake = false;
        _sfx.spatialBlend = 0f;
        _sfx.volume = 0.9f;

        // 市井人声底噪层（AMB_Town 存在才播放，音量压低垫在 BGM 下）
        var amb = Load("AMB_Town");
        if (amb != null)
        {
            _amb = gameObject.AddComponent<AudioSource>();
            _amb.clip = amb;
            _amb.loop = true;
            _amb.playOnAwake = false;
            _amb.spatialBlend = 0f;
            _amb.volume = 0.15f;
            _amb.Play();
        }

        var bgm = Load("BGM_Town");
        if (bgm != null)
        {
            _bgm.clip = bgm;
            _bgm.Play();
        }
    }

    /// <summary>播放一次性音效；clip 未就绪时静默跳过。</summary>
    public static void Play(string name, float volume = 1f)
    {
        var a = I;
        if (!a._cache.TryGetValue(name, out var clip))
        {
            clip = a.Load(name);
            if (clip == null) return;
            a._cache[name] = clip;
        }
        a._sfx.PlayOneShot(clip, volume);
    }

    /// <summary>开场演出用：BGM 从极低音量淡入到正常水平（约 4s），只影响本次播放。</summary>
    public static void FadeInBgm()
    {
        var a = I;
        if (a._bgm == null || a._bgm.clip == null) return;
        a.StartCoroutine(a.FadeInBgmCo());
    }

    private IEnumerator FadeInBgmCo()
    {
        const float target = 0.32f;
        const float duration = 4f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            if (_bgm == null) yield break;
            _bgm.volume = Mathf.Lerp(0.04f, target, Mathf.SmoothStep(0f, 1f, t / duration));
            yield return null;
        }
        if (_bgm != null) _bgm.volume = target;
    }

    private AudioClip Load(string name)
    {
        var clip = Resources.Load<AudioClip>("Audio/" + name);
        if (clip == null) Debug.Log($"[AudioManager] {name} 未找到（Resources/Audio/{name}.wav），跳过");
        return clip;
    }
}
