// ***********************************************************************
// Assembly         : RecordingBot.Services
// Author           : JasonTheDeveloper
// Created          : 09-07-2020
//
// Last Modified By : dannygar
// Last Modified On : 09-07-2020
// ***********************************************************************
// <copyright file="BotMediaStream.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
// </copyright>
// <summary>The bot media stream.</summary>
// ***********************************************************************-

using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using Microsoft.Skype.Internal.Media.Services.Common;
using RecordingBot.Services.Contract;
using RecordingBot.Services.Media;
using RecordingBot.Services.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;


namespace RecordingBot.Services.Bot
{
    /// <summary>
    /// Class responsible for streaming audio and video.
    /// </summary>
    public class BotMediaStream : ObjectRootDisposable
    {
        /// <summary>
        /// The participants
        /// </summary>
        internal List<IParticipant> participants;

        /// <summary>
        /// The audio socket
        /// </summary>
        private readonly IAudioSocket _audioSocket;

        /// <summary>
        /// The video socket
        /// </summary>
        private readonly IMediaStream _mediaStream;

        /// <summary>
        /// The event publisher
        /// </summary>
        private readonly IEventPublisher _eventPublisher;

        /// <summary>
        /// The call identifier
        /// </summary>
        private readonly string _callId;

        private readonly IVideoSocket mainVideoSocket;
        private readonly IVideoSocket vbssSocket;
        private readonly List<IVideoSocket> videoSockets;
        private readonly IGraphLogger logger;
        private readonly ILocalMediaSession mediaSession;
        private List<VideoFormat> vbssKnownSupportedFormats;
        private AudioVideoFramePlayer vbssFramePlayer;
        private List<VideoMediaBuffer> vbssMediaBuffers = new List<VideoMediaBuffer>();
        private AudioVideoFramePlayerSettings audioVideoFramePlayerSettings;
        private int shutdown;

        private List<VideoFormat> videoKnownSupportedFormats;
        private List<AudioMediaBuffer> audioMediaBuffers = new List<AudioMediaBuffer>();
        private AudioVideoFramePlayer audioVideoFramePlayer;
        private readonly TaskCompletionSource<bool> videoSendStatusActive;
        private List<VideoMediaBuffer> videoMediaBuffers = new List<VideoMediaBuffer>();
        private readonly object mLock = new object();
        private long audioTick;
        private long videoTick;
        private long mediaTick;

        /// <summary>
        /// Return the last read 'audio quality of experience data' in a serializable structure
        /// </summary>
        /// <value>The audio quality of experience data.</value>
        public SerializableAudioQualityOfExperienceData AudioQualityOfExperienceData { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="BotMediaStream" /> class.
        /// </summary>
        /// <param name="mediaSession">he media session.</param>
        /// <param name="callId">The call identity</param>
        /// <param name="logger">The logger.</param>
        /// <param name="eventPublisher">Event Publisher</param>
        /// <param name="settings">Azure settings</param>
        /// <exception cref="InvalidOperationException">A mediaSession needs to have at least an audioSocket</exception>
        public BotMediaStream(
            ILocalMediaSession mediaSession,
            string callId,
            IGraphLogger logger,
            IEventPublisher eventPublisher,
            IAzureSettings settings
        )
            : base(logger)
        {
            ArgumentVerifier.ThrowOnNullArgument(mediaSession, nameof(mediaSession));
            ArgumentVerifier.ThrowOnNullArgument(logger, nameof(logger));
            ArgumentVerifier.ThrowOnNullArgument(settings, nameof(settings));

            this.participants = new List<IParticipant>();

            _eventPublisher = eventPublisher;
            _callId = callId;
            _mediaStream = new MediaStream(
                settings,
                logger,
                mediaSession.MediaSessionId.ToString()
            );

            this.logger = logger;
            this.mediaSession = mediaSession;

            // Subscribe to the audio media.
            this._audioSocket = mediaSession.AudioSocket;
            if (this._audioSocket == null)
            {
                throw new InvalidOperationException("A mediaSession needs to have at least an audioSocket");
            }

            this._audioSocket.AudioMediaReceived += this.OnAudioMediaReceived;

            this.mainVideoSocket = mediaSession.VideoSockets?.FirstOrDefault();
            if (this.mainVideoSocket != null)
            {
                this.mainVideoSocket.VideoSendStatusChanged += this.OnVideoSendStatusChanged;
                this.mainVideoSocket.VideoKeyFrameNeeded += this.OnVideoKeyFrameNeeded;
            }

            this.videoSockets = this.mediaSession.VideoSockets?.ToList();

            this.vbssSocket = this.mediaSession.VbssSocket;
            if (this.vbssSocket != null)
            {
                this.vbssSocket.VideoSendStatusChanged += this.OnVbssSocketSendStatusChanged;
            }
        }

        /// <summary>
        /// Gets the participants.
        /// </summary>
        /// <returns>List&lt;IParticipant&gt;.</returns>
        public List<IParticipant> GetParticipants()
        {
            return participants;
        }

        /// <summary>
        /// Gets the audio quality of experience data.
        /// </summary>
        /// <returns>SerializableAudioQualityOfExperienceData.</returns>
        public SerializableAudioQualityOfExperienceData GetAudioQualityOfExperienceData()
        {
            AudioQualityOfExperienceData = new SerializableAudioQualityOfExperienceData(this._callId, this._audioSocket.GetQualityOfExperienceData());
            return AudioQualityOfExperienceData;
        }

        /// <summary>
        /// Stops the media.
        /// </summary>
        public async Task StopMedia()
        {
            await _mediaStream.End();
            // Event - Stop media occurs when the call stops recording
            _eventPublisher.Publish("StopMediaStream", "Call stopped recording");
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            // Event Dispose of the bot media stream object
            _eventPublisher.Publish("MediaStreamDispose", disposing.ToString());

            base.Dispose(disposing);

            this._audioSocket.AudioMediaReceived -= this.OnAudioMediaReceived;
        }

        /// <summary>
        /// Receive audio from subscribed participant.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The audio media received arguments.</param>
        private async void OnAudioMediaReceived(object sender, AudioMediaReceivedEventArgs e)
        {
            this.GraphLogger.Info($"Received Audio: [AudioMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp})]");

            try
            {
                await _mediaStream.AppendAudioBuffer(e.Buffer, this.participants);
                e.Buffer.Dispose();
            }
            catch (Exception ex)
            {
                this.GraphLogger.Error(ex);
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        /// <summary>
        /// Callback for informational updates from the media plaform about video status changes.
        /// Once the Status becomes active, then video can be sent.
        /// </summary>
        /// <param name="sender">The video socket.</param>
        /// <param name="e">Event arguments.</param>
        private void OnVideoSendStatusChanged(object sender, VideoSendStatusChangedEventArgs e)
        {
            this.logger.Info($"[VideoSendStatusChangedEventArgs(MediaSendStatus=<{e.MediaSendStatus}>]");

            if (e.MediaSendStatus == MediaSendStatus.Active)
            {
                this.logger.Info($"[VideoSendStatusChangedEventArgs(MediaSendStatus=<{e.MediaSendStatus}>;PreferredVideoSourceFormat=<{string.Join(";", e.PreferredEncodedVideoSourceFormats.ToList())}>]");

                var previousSupportedFormats = (this.videoKnownSupportedFormats != null && this.videoKnownSupportedFormats.Any()) ? this.videoKnownSupportedFormats :
                new List<VideoFormat>();
                this.videoKnownSupportedFormats = e.PreferredEncodedVideoSourceFormats.ToList();

                // when this is false it means that we have received a new event with different videoFormats
                // the behavior for this bot is to clean up the previous enqueued media and push the new formats,
                // starting from beginning
                if (!this.videoSendStatusActive.TrySetResult(true))
                {
                    if (this.videoKnownSupportedFormats != null && this.videoKnownSupportedFormats.Any() &&

                        // here it means we got a new video fromat so we need to restart the player
                        this.videoKnownSupportedFormats.Select(x => x.GetId()).Except(previousSupportedFormats.Select(y => y.GetId())).Any())
                    {
                        // we restart the player
                        this.audioVideoFramePlayer?.ClearAsync().ForgetAndLogExceptionAsync(this.logger);

                        this.logger.Info($"[VideoSendStatusChangedEventArgs(MediaSendStatus=<{e.MediaSendStatus}> enqueuing new formats: {string.Join(";", this.videoKnownSupportedFormats)}]");

                        // Create the AV buffers
                        var currentTick = DateTime.Now.Ticks;
                        this.CreateAVBuffers(currentTick, replayed: false);

                        this.audioVideoFramePlayer?.EnqueueBuffersAsync(this.audioMediaBuffers, this.videoMediaBuffers).ForgetAndLogExceptionAsync(this.logger);
                    }
                }
            }
            else if (e.MediaSendStatus == MediaSendStatus.Inactive)
            {
                if (this.videoSendStatusActive.Task.IsCompleted && this.audioVideoFramePlayer != null)
                {
                    this.audioVideoFramePlayer?.ClearAsync().ForgetAndLogExceptionAsync(this.logger);
                }
            }
        }

        /// <summary>
        /// If the application has configured the VideoSocket to receive encoded media, this
        /// event is raised each time a key frame is needed. Events are serialized, so only
        /// one event at a time is raised to the app.
        /// </summary>
        /// <param name="sender">Video socket.</param>
        /// <param name="e">Event args specifying the socket id, media type and video formats for which key frame is being requested.</param>
        private void OnVideoKeyFrameNeeded(object sender, VideoKeyFrameNeededEventArgs e)
        {
            this.logger.Info($"[VideoKeyFrameNeededEventArgs(MediaType=<{{e.MediaType}}>;SocketId=<{{e.SocketId}}>VideoFormats=<{string.Join(";", e.VideoFormats.ToList())}>] calling RequestKeyFrame on the videoSocket");
        }

        /// <summary>
        /// Subscription for video and vbss.
        /// </summary>
        /// <param name="mediaType">vbss or video.</param>
        /// <param name="mediaSourceId">The video source Id.</param>
        /// <param name="videoResolution">The preferred video resolution.</param>
        /// <param name="socketId">Socket id requesting the video. For vbss it is always 0.</param>
        public void Subscribe(MediaType mediaType, uint mediaSourceId, VideoResolution videoResolution, uint socketId = 0)
        {
            try
            {
                this.ValidateSubscriptionMediaType(mediaType);

                this.logger.Info($"Subscribing to the video source: {mediaSourceId} on socket: {socketId} with the preferred resolution: {videoResolution} and mediaType: {mediaType}");
                if (mediaType == MediaType.Vbss)
                {
                    if (this.vbssSocket == null)
                    {
                        this.logger.Warn($"vbss socket not initialized");
                    }
                    else
                    {
                        this.vbssSocket.Subscribe(videoResolution, mediaSourceId);
                    }
                }
                else if (mediaType == MediaType.Video)
                {
                    if (this.videoSockets == null)
                    {
                        this.logger.Warn($"video sockets were not created");
                    }
                    else
                    {
                        this.videoSockets[(int)socketId].Subscribe(videoResolution, mediaSourceId);
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.Error(ex, $"Video Subscription failed for the socket: {socketId} and MediaSourceId: {mediaSourceId} with exception");
            }
        }

        /// <summary>
        /// Unsubscribe to video.
        /// </summary>
        /// <param name="mediaType">vbss or video.</param>
        /// <param name="socketId">Socket id. For vbss it is always 0.</param>
        public void Unsubscribe(MediaType mediaType, uint socketId = 0)
        {
            try
            {
                this.ValidateSubscriptionMediaType(mediaType);

                this.logger.Info($"Unsubscribing to video for the socket: {socketId} and mediaType: {mediaType}");

                if (mediaType == MediaType.Vbss)
                {
                    this.vbssSocket?.Unsubscribe();
                }
                else if (mediaType == MediaType.Video)
                {
                    this.videoSockets[(int)socketId]?.Unsubscribe();
                }
            }
            catch (Exception ex)
            {
                this.logger.Error(ex, $"Unsubscribing to video failed for the socket: {socketId} with exception");
            }
        }

        /// <summary>
        /// Ensure media type is video or VBSS.
        /// </summary>
        /// <param name="mediaType">Media type to validate.</param>
        private void ValidateSubscriptionMediaType(MediaType mediaType)
        {
            if (mediaType != MediaType.Vbss && mediaType != MediaType.Video)
            {
                throw new ArgumentOutOfRangeException($"Invalid mediaType: {mediaType}");
            }
        }

        /// <summary>
        /// Performs action when the vbss socket send status changed event is received.
        /// </summary>
        /// <param name="sender">
        /// The sender.
        /// </param>
        /// <param name="e">
        /// The video send status changed event arguments.
        /// </param>
        private void OnVbssSocketSendStatusChanged(object sender, VideoSendStatusChangedEventArgs e)
        {
            this.logger.Info($"[VbssSendStatusChangedEventArgs(MediaSendStatus=<{e.MediaSendStatus}>]");

            if (e.MediaSendStatus == MediaSendStatus.Active)
            {
                this.logger.Info($"[VbssSendStatusChangedEventArgs(MediaSendStatus=<{e.MediaSendStatus}>;PreferredVideoSourceFormat=<{string.Join(";", e.PreferredEncodedVideoSourceFormats.ToList())}>]");

                var previousSupportedFormats = (this.vbssKnownSupportedFormats != null && this.vbssKnownSupportedFormats.Any()) ? this.vbssKnownSupportedFormats :
                   new List<VideoFormat>();
                this.vbssKnownSupportedFormats = e.PreferredEncodedVideoSourceFormats.ToList();

                if (this.vbssFramePlayer == null)
                {
                    this.CreateVbssFramePlayer();
                }

                // when this is false it means that we have received a new event with different videoFormats
                // the behavior for this bot is to clean up the previous enqueued media and push the new formats,
                // starting from beginning
                else
                {
                    // we restart the player
                    this.vbssFramePlayer?.ClearAsync().ForgetAndLogExceptionAsync(this.logger);
                }

                // enqueue video buffers
                this.logger.Info($"[VbssSendStatusChangedEventArgs(MediaSendStatus=<{e.MediaSendStatus}> enqueuing new formats: {string.Join(";", this.vbssKnownSupportedFormats)}]");

                // Create the video buffers
                this.vbssMediaBuffers = MediaBuffers.CreateVideoMediaBuffers(DateTime.Now.Ticks, this.vbssKnownSupportedFormats, true, this.logger);
                this.vbssFramePlayer?.EnqueueBuffersAsync(new List<AudioMediaBuffer>(), this.vbssMediaBuffers).ForgetAndLogExceptionAsync(this.logger);
            }
            else if (e.MediaSendStatus == MediaSendStatus.Inactive)
            {
                this.vbssFramePlayer?.ClearAsync().ForgetAndLogExceptionAsync(this.logger);
            }
        }

        /// <summary>
        /// Callback handler for the lowOnFrames event that the vbss frame player will raise when there are no more frames to stream.
        /// The behavior is to enqueue more frames.
        /// </summary>
        /// <param name="sender">The vbss frame player.</param>
        /// <param name="e">LowOnframes eventArgs.</param>
        private void OnVbssPlayerLowOnFrames(object sender, LowOnFramesEventArgs e)
        {
            if (this.shutdown != 1)
            {
                this.logger.Info($"Low on frames event raised for the vbss player, remaining lenght is {e.RemainingMediaLengthInMS} ms");

                // Create the video buffers
                this.vbssMediaBuffers = MediaBuffers.CreateVideoMediaBuffers(DateTime.Now.Ticks, this.vbssKnownSupportedFormats, true, this.logger);
                this.vbssFramePlayer?.EnqueueBuffersAsync(new List<AudioMediaBuffer>(), this.vbssMediaBuffers).ForgetAndLogExceptionAsync(this.logger);
                this.logger.Info("enqueued more frames in the vbssFramePlayer");
            }
        }

        /// <summary>
        /// Create audio video buffers.
        /// </summary>
        /// <param name="referenceTick">Current clock tick.</param>
        /// <param name="replayed">If frame is replayed.</param>
        private void CreateAVBuffers(long referenceTick, bool replayed)
        {
            this.logger.Info("Creating AudioVideoBuffers");

            lock (this.mLock)
            {
                this.videoMediaBuffers = MediaBuffers.CreateVideoMediaBuffers(
                    referenceTick,
                    this.videoKnownSupportedFormats,
                    replayed,
                    this.logger);

                this.audioMediaBuffers = MediaBuffers.CreateAudioMediaBuffers(
                    referenceTick,
                    replayed,
                    this.logger);

                // update the tick for next iteration
                this.audioTick = this.audioMediaBuffers.Last().Timestamp;
                this.videoTick = this.videoMediaBuffers.Last().Timestamp;
                this.mediaTick = Math.Max(this.audioTick, this.videoTick);
            }
        }

        /// <summary>
        /// Creates the vbss player that will stream the video buffers for the sharer.
        /// </summary>
        private void CreateVbssFramePlayer()
        {
            try
            {
                this.logger.Info("Creating the vbss FramePlayer");
                this.audioVideoFramePlayerSettings =
                    new AudioVideoFramePlayerSettings(new AudioSettings(20), new VideoSettings(), 1000);
                this.vbssFramePlayer = new AudioVideoFramePlayer(
                    null,
                    (VideoSocket)this.vbssSocket,
                    this.audioVideoFramePlayerSettings);

                this.vbssFramePlayer.LowOnFrames += this.OnVbssPlayerLowOnFrames;
            }
            catch (Exception ex)
            {
                this.logger.Error(ex, $"Failed to create the vbssFramePlayer with exception {ex}");
            }
        }
    }
}
