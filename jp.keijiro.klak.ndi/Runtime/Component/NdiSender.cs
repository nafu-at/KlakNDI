using UnityEngine;
using UnityEngine.Rendering;

namespace Klak.Ndi {

    [ExecuteInEditMode]
    public sealed partial class NdiSender : MonoBehaviour
    {
    #region Sender objects

        Interop.Send _send;
        ReadbackPool _pool;
        FormatConverter _converter;
        System.Action<AsyncGPUReadbackRequest> _onReadback;

        void PrepareSenderObjects()
        {
            // Game view capture method: Borrow the shared sender instance.
            if (_send == null && captureMethod == CaptureMethod.GameView)
                _send = SharedInstance.GameViewSend;

            // Private object initialization
            if (_send == null) _send = Interop.Send.Create(ndiName);
            if (_pool == null) _pool = new ReadbackPool();
            if (_converter == null) _converter = new FormatConverter(_resources);
            if (_onReadback == null) _onReadback = OnReadback;
        }

        void ReleaseSenderObjects()
        {
            // Total synchronization: This may cause a frame hiccup, but it's
            // needed to dispose the readback buffers safely.
            AsyncGPUReadback.WaitAllRequests();

            // Game view capture method: Leave the sender instance without
            // disposing (we're not the owner) but synchronize it. It's needed to
            // dispose the readback buffers safely too.
            if (SharedInstance.IsGameViewSend(_send))
            {
                _send.SendVideoAsync(); // Sync by null-send
                _send = null;
            }

            // Private objet disposal
            _send?.Dispose();
            _send = null;

            _pool?.Dispose();
            _pool = null;

            _converter?.Dispose();
            _converter = null;

            // We don't dispose _onReadback because it's reusable.
        }

    #endregion

    #region Capture coroutine for the Texture/GameView capture methods

        System.Collections.IEnumerator CaptureCoroutine()
        {
            for (var eof = new WaitForEndOfFrame(); true;)
            {
                // Wait for the end of the frame.
                yield return eof;

                PrepareSenderObjects();

                // Texture capture method
                if (captureMethod == CaptureMethod.Texture && sourceTexture != null)
                {
                    var (w, h) = (sourceTexture.width, sourceTexture.height);

                    // Pixel format conversion
                    var buffer = _converter.Encode(sourceTexture, keepAlpha, true);

                    // Readback entry allocation and request
                    _pool.NewEntry(w, h, keepAlpha, metadata)
                        .RequestReadback(buffer, _onReadback);
                }

                // Game View capture method
                if (captureMethod == CaptureMethod.GameView)
                {
                    // Game View screen capture with a temporary RT
                    var (w, h) = (Screen.width, Screen.height);
                    var tempRT = RenderTexture.GetTemporary(w, h, 0);
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(tempRT);

                    // Pixel format conversion
                    var buffer = _converter.Encode(tempRT, keepAlpha, false);
                    RenderTexture.ReleaseTemporary(tempRT);

                    // Readback entry allocation and request
                    _pool.NewEntry(w, h, keepAlpha, metadata)
                        .RequestReadback(buffer, _onReadback);
                }
            }
        }

    #endregion

    #region SRP camera capture callback for the Camera capture method

        void OnCameraCapture(RenderTargetIdentifier source, CommandBuffer cb)
        {
            // A SRP may call this callback after object destruction. We can
            // exclude those cases by null-checking _attachedCamera.
            if (_attachedCamera == null) return;

            PrepareSenderObjects();

            // Pixel format conversion
            var (w, h) = (sourceCamera.pixelWidth, sourceCamera.pixelHeight);
            var buffer = _converter.Encode(cb, source, w, h, keepAlpha, true);

            // Readback entry allocation and request
            _pool.NewEntry(w, h, keepAlpha, metadata)
                .RequestReadback(buffer, _onReadback);
        }

    #endregion

    #region GPU readback completion callback

        unsafe void OnReadback(AsyncGPUReadbackRequest req)
        {
            // Readback entry retrieval
            var entry = _pool.FindEntry(req.GetData<byte>());
            if (entry == null) return;

            // Invalid state detection
            if (req.hasError || _send == null || _send.IsInvalid || _send.IsClosed)
            {
                // Do nothing but release the readback entry.
                _pool.Free(entry);
                return;
            }

            // Frame data
            var frame = new Interop.VideoFrame
            {
                Width = entry.Width,
                Height = entry.Height,
                LineStride = entry.Stride,
                FourCC = entry.FourCC,
                FrameFormat = Interop.FrameFormat.Progressive,
                Data = entry.ImagePointer,
                _Metadata = entry.MetadataPointer
            };

            // Async-send initiation
            // This causes a synchronization for the last frame -- i.e., It locks
            // the thread if the last frame is still under processing.
            _send.SendVideoAsync(frame);

            // We don't need the last frame anymore. Free it.
            _pool.FreeMarkedEntry();

            // Mark this frame to get freed in the next frame.
            _pool.Mark(entry);
        }

    #endregion

    #region Component state controller

        Camera _attachedCamera;

        // Component state reset without NDI object disposal
        internal void ResetState(bool willBeActive)
        {
            // Camera capture coroutine termination
            // We use this to kill only a single coroutine. It may sound like
            // overkill, but I think there is no side effect in doing so.
            StopAllCoroutines();

        #if KLAK_NDI_HAS_SRP

        // A SRP may call this callback after camera destruction. We can
        // exclude those cases by null-checking _attachedCamera.
        if (_attachedCamera != null)
            CameraCaptureBridge.RemoveCaptureAction(_attachedCamera, OnCameraCapture);

        #endif

            _attachedCamera = null;

            // The following part of code is to activate the subcomponents. We can
            // break here if willBeActive is false.
            if (!willBeActive) return;

            if (captureMethod == CaptureMethod.Camera)
            {
            #if KLAK_NDI_HAS_SRP

            // Camera capture callback setup
            if (sourceCamera != null)
                CameraCaptureBridge.AddCaptureAction(sourceCamera, OnCameraCapture);

            #endif

                _attachedCamera = sourceCamera;
            }
            else
            {
                // Capture coroutine initiation
                StartCoroutine(CaptureCoroutine());
            }
        }

        // Component state reset with NDI object disposal
        internal void Restart(bool willBeActivate)
        {
            ResetState(willBeActivate);
            ReleaseSenderObjects();
        }

        internal void ResetState() => ResetState(isActiveAndEnabled);
        internal void Restart() => Restart(isActiveAndEnabled);

    #endregion

    #region MonoBehaviour implementation

        void OnEnable() => ResetState();
        void OnDisable() => Restart(false);
        void OnDestroy() => Restart(false);

    #endregion

    #region Sound Sender
        private int numSamples = 0;
        private int numChannels = 0;
        private float[] samples = new float[1];

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (data.Length == 0 || channels == 0) return;

            unsafe
            {
                bool settingsChanged = false;
                int tempSamples = data.Length / channels;

                if (tempSamples != numSamples)
                {
                    settingsChanged = true;
                    numSamples = tempSamples;
                    //PluginEntry.SetNumSamples(_plugin, numSamples);
                }

                if (channels != numChannels)
                {
                    settingsChanged = true;
                    numChannels = channels;
                    //PluginEntry.SetAudioChannels(_plugin, channels);
                }

                if (settingsChanged)
                {
                    System.Array.Resize<float>(ref samples, numSamples * numChannels);
                }

                for (int ch = 0; ch < numChannels; ch++)
                {
                    for (int i = 0; i < numSamples; i++)
                    {
                        samples[numSamples * ch + i] = data[i * numChannels + ch];
                    }
                }

                fixed (float* p = samples)
                {
                    //PluginEntry.SetAudioData(_plugin, (IntPtr)p);
                    var frame = new Interop.AudioFrame
                    {
                        SampleRate = 48000,
                        NoChannels = channels,
                        NoSamples = numSamples,
                        ChannelStrideInBytes = numSamples * sizeof(float),
                        Data = (System.IntPtr)p
                    };

                    if (_send != null)
                    {
                        _send.SendAudio(frame);
                    }
                }

                //if (audioEnabled && pluginReady) PluginEntry.SendAudio(_plugin);
            }
        }

    #endregion

}

} // namespace Klak.Ndi
