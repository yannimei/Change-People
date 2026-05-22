using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;
using SimpleWebRTC;
using TMPro;
using UnityEngine.Rendering;

public class GameManager : MonoBehaviour
{
    [Header("UI")]
    [Tooltip("Start button in menu (optional, can trigger experience start manually).")]
    [SerializeField] private Button startButton;

    [Tooltip("UI fader for fading the main menu out at experience start.")]
    [SerializeField] private UIFader menuFader;

    [Tooltip("UI fader for showing informational text after portal opens.")]
    [SerializeField] private UIFader textFader;

    [Tooltip("Status text shown during model selection / reference-image caching. Must live OUTSIDE the menu so it stays visible when the menu fades out.")]
    [SerializeField] private TMP_Text statusText;

    [Header("Scene")]
    [Tooltip("Portal controller used for scaling and revealing the portal.")]
    [SerializeField] private PortalController portal;

    [Tooltip("Parent transform holding RawImages that display remote video.")]
    [SerializeField] private Transform streamParent;

    [Tooltip("OVR Passthrough layer to animate transparency when experience starts.")]
    [SerializeField] private OVRPassthroughLayer passthroughLayer;

    [Tooltip("Canvas displaying local webcam feed for WebRTC.")]
    [SerializeField] private GameObject WebRTCWebcamCanvas;

    [Tooltip("Camera used for recording or capturing effects (optional).")]
    [SerializeField] private Camera recordingCamera;

    [Tooltip("Effect cone GameObject that appears during wave/intro effects.")]
    [SerializeField] private GameObject effectCone;

    [Header("Audio/Visual")]
    [Tooltip("Set of looping audio clips used for force-field sounds.")]
    [SerializeField] private AudioClip[] forceFieldSounds;

    [Tooltip("AudioSource used to play force-field and wave sounds.")]
    [SerializeField] private AudioSource audioSource;

    [Tooltip("Material controlling intro shader effect. Needs '_CustomTime' float property.")]
    [SerializeField] private Material introEffectMaterial;

    [Header("Networking")]
    [Tooltip("WebRTC connection that handles model selection and video streaming.")]
    [SerializeField] private WebRTCConnection webRtcConnection;

    private enum ExperienceState
    {
        WaitingForSelection,
        ModelSelected,
        Running
    }

    private ExperienceState _state = ExperienceState.WaitingForSelection;

    private bool _didStart;
    private int _currentSoundIndex;
    private bool _animatingIntroEffect;
    private bool _videoTransmissionPending;
    private Tween _passthroughTween;
    private Tween _forceFieldSoundTween;
    private static readonly int CustomTime = Shader.PropertyToID("_CustomTime");

    private bool _hasModelSelection;
    private bool _selectedUseLucy;
    private float _bothTriggersHeldTime;
    private const float BothTriggersResetDuration = 2f;

    private void Start()
    {
        ShowModelSelectionPrompt();

        #if !UNITY_EDITOR
        if (WebRTCWebcamCanvas != null)
            WebRTCWebcamCanvas.SetActive(true);

        RemoveSkybox();

        UnityEngine.XR.XRSettings.eyeTextureResolutionScale = 2f;

        if (GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp)
            urp.renderScale = 2f;
        #endif
    }

    private void OnDestroy()
    {
        _passthroughTween?.Kill();
        _forceFieldSoundTween?.Kill();
    }

    private void ShowModelSelectionPrompt()
    {
        _state = ExperienceState.WaitingForSelection;
        _hasModelSelection = false;
        Debug.Log("Model selection: Press A for Mirage or B for Lucy");
    }

    private void SelectModel(bool useLucy)
    {
        _hasModelSelection = true;
        _selectedUseLucy = useLucy;
        _state = ExperienceState.ModelSelected;

        menuFader?.FadeOut();

        if (useLucy && webRtcConnection)
        {
            SetStatus("you select lucy-2.1 model, wait for caching...");
            webRtcConnection.PrecacheReferenceImages(
                () => SetStatus("press x to connect"),
                err => SetStatus($"image caching failed ({err})\npress x to connect"));
        }
        else
        {
            SetStatus($"{(useLucy ? "Lucy-2.1" : "Mirage")} selected — press x to connect");
        }
    }

    private void SetStatus(string message)
    {
        var hasMessage = !string.IsNullOrEmpty(message);

        if (statusText != null)
        {
            statusText.text = message;
        }

        // statusText lives under textFader's group, which starts hidden (alpha 0),
        // so fade it in to actually show the message and out when cleared.
        if (hasMessage)
        {
            textFader?.Show();
            Debug.Log(message);
        }
        else
        {
            textFader?.Hide();
        }
    }

    private void ConnectSelectedModel()
    {
        if (!_hasModelSelection) return;

        if (!webRtcConnection)
        {
            Debug.LogError("WebRTCConnection reference is missing! Cannot start experience.");
            return;
        }

        webRtcConnection.SetModelChoice(_selectedUseLucy);
        webRtcConnection.Connect();
        Debug.Log($"Connecting model: {webRtcConnection.GetSelectedModelName()}");

        StartExperience();
    }

    private void DisconnectAndShowPassthrough()
    {
        if (webRtcConnection) webRtcConnection.Disconnect();
        RevertToPassthrough();
        _state = ExperienceState.ModelSelected;
        SetStatus($"{(_selectedUseLucy ? "Lucy-2.1" : "Mirage")} disconnected — press x to reconnect");
    }

    private void ResetToSelectionPrompt()
    {
        if (webRtcConnection) webRtcConnection.Disconnect();
        RevertToPassthrough();
        SetStatus("");
        menuFader?.FadeIn();
        ShowModelSelectionPrompt();
    }

    private void RevertToPassthrough()
    {
        _didStart = false;
        _videoTransmissionPending = false;

        StopForceFieldSounds();
        StopIntroEffect();

        if (streamParent)
        {
            // Destroy the runtime-created receiving RawImages so a fresh reconnect
            // doesn't accumulate orphan children from the previous session.
            foreach (var rawImage in streamParent.GetComponentsInChildren<RawImage>(includeInactive: true))
            {
                if (rawImage.gameObject.name.EndsWith("-Receiving-RawImage"))
                {
                    Destroy(rawImage.gameObject);
                }
            }
            streamParent.gameObject.SetActive(false);
        }
        if (effectCone) effectCone.SetActive(false);

        if (portal && portal.fader) portal.fader.FadeTo(0f, duration: 0.5f);
        textFader?.FadeOut();

        _passthroughTween?.Kill();
        if (passthroughLayer)
        {
            _passthroughTween = DOTween.To(
                () => passthroughLayer.textureOpacity,
                x => passthroughLayer.textureOpacity = x,
                1f, 0.5f);
        }
    }

    private void StartExperience()
    {
        _didStart = true;
        _state = ExperienceState.Running;

        SetStatus("");
        menuFader?.FadeOut();
        portal?.Show();
        if (effectCone)
        {
            effectCone.SetActive(true);
        }

        if (passthroughLayer)
        {
            _passthroughTween = DOTween.To(
                () => passthroughLayer.textureOpacity,
                x => passthroughLayer.textureOpacity = x,
                0.25f, 1f);
        }

        if (_videoTransmissionPending)
        {
            DOVirtual.DelayedCall(3f, OnVideoTransmissionReceived);
        }

        StartForceFieldSounds();
        StartIntroEffect();
    }

    public void OnVideoTransmissionReceived()
    {
        if (!_didStart)
        {
            _videoTransmissionPending = true;
            return;
        }

        _videoTransmissionPending = false;
        StopForceFieldSounds();
        StopIntroEffect();

        if (streamParent)
        {
            streamParent.gameObject.SetActive(true);
        }

        portal?.Expand();
        if (effectCone)
        {
            effectCone.SetActive(false);
        }

        textFader?.FadeIn(delay: 3f);

        if (!portal || !streamParent)
        {
            return;
        }

        foreach (var rawImage in streamParent.GetComponentsInChildren<RawImage>())
        {
            rawImage.material = portal.portalMaterial;
        }
    }

    private void Update()
    {
        UpdateBothTriggersReset();

        switch (_state)
        {
            case ExperienceState.WaitingForSelection:
                if (OVRInput.GetDown(OVRInput.Button.One)) // A = Mirage
                {
                    SelectModel(useLucy: false);
                }
                else if (OVRInput.GetDown(OVRInput.Button.Two)) // B = Lucy
                {
                    SelectModel(useLucy: true);
                }
                break;

            case ExperienceState.ModelSelected:
                if (OVRInput.GetDown(OVRInput.Button.One)) // A = Mirage
                {
                    SelectModel(useLucy: false);
                }
                else if (OVRInput.GetDown(OVRInput.Button.Two)) // B = Lucy
                {
                    SelectModel(useLucy: true);
                }
                else if (OVRInput.GetDown(OVRInput.Button.Three)) // X = Connect
                {
                    ConnectSelectedModel();
                }
                break;

            case ExperienceState.Running:
                if (OVRInput.GetDown(OVRInput.Button.Four)) // Y = Disconnect
                {
                    DisconnectAndShowPassthrough();
                }
                else if (OVRInput.GetDown(OVRInput.Button.One)) // A = wave + apply reference image (Lucy)
                {
                    ShowWave();
                    if (webRtcConnection) webRtcConnection.SendReferenceImage();
                }
                else if (OVRInput.GetDown(OVRInput.Button.Two)) // B = wave + next text prompt
                {
                    ShowWave();
                    if (webRtcConnection) webRtcConnection.SendNextPrompt(true);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void UpdateBothTriggersReset()
    {
        var leftTrigger = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger);
        var rightTrigger = OVRInput.Get(OVRInput.Button.SecondaryIndexTrigger);

        if (leftTrigger && rightTrigger)
        {
            _bothTriggersHeldTime += Time.deltaTime;
            if (_bothTriggersHeldTime >= BothTriggersResetDuration && _state != ExperienceState.WaitingForSelection)
            {
                _bothTriggersHeldTime = 0f;
                ResetToSelectionPrompt();
            }
        }
        else
        {
            _bothTriggersHeldTime = 0f;
        }
    }

    private void ShowWave()
    {
        if (effectCone) effectCone.SetActive(true);
        StartCoroutine(DisableEffectConeAfterDelay());

        if (audioSource && forceFieldSounds.Length > 0)
        {
            audioSource.clip = forceFieldSounds[_currentSoundIndex];
            audioSource.pitch = 0.75f;
            audioSource.Play();
            _currentSoundIndex = (_currentSoundIndex + 1) % forceFieldSounds.Length;
        }

        StartIntroEffect();
    }

    private IEnumerator DisableEffectConeAfterDelay()
    {
        yield return new WaitForSeconds(1.5f);
        StopIntroEffect();
        if (effectCone)
        {
            effectCone.SetActive(false);
        }
    }

    private void StartForceFieldSounds()
    {
        if (forceFieldSounds.Length == 0 || !audioSource)
        {
            return;
        }

        _currentSoundIndex = 0;
        PlayNextForceFieldSound();
    }

    private void StopForceFieldSounds()
    {
        _forceFieldSoundTween?.Kill();
        audioSource?.Stop();
    }

    private void PlayNextForceFieldSound()
    {
        if (!audioSource || forceFieldSounds.Length == 0)
        {
            return;
        }

        audioSource.clip = forceFieldSounds[_currentSoundIndex];
        audioSource.pitch = 0.75f;
        audioSource.Play();

        const float clipLength = 1.5f;
        _forceFieldSoundTween = DOVirtual.DelayedCall(clipLength, () =>
        {
            _currentSoundIndex = (_currentSoundIndex + 1) % forceFieldSounds.Length;
            PlayNextForceFieldSound();
        });
    }

    private void StartIntroEffect()
    {
        if (!introEffectMaterial)
        {
            return;
        }

        _animatingIntroEffect = true;
        introEffectMaterial.SetFloat(CustomTime, 0f);
        StartCoroutine(UpdateIntroEffectTime());
    }

    private void StopIntroEffect()
    {
        _animatingIntroEffect = false;
    }

    private IEnumerator UpdateIntroEffectTime()
    {
        var startTime = Time.time;
        while (_animatingIntroEffect && introEffectMaterial)
        {
            var elapsedTime = Time.time - startTime;
            introEffectMaterial.SetFloat(CustomTime, elapsedTime);
            yield return null;
        }
    }
    
    private void RemoveSkybox()
    {
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        DynamicGI.UpdateEnvironment();
    }
}
