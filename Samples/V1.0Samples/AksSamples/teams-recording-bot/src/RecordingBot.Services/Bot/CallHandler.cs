// ***********************************************************************
// Assembly         : RecordingBot.Services
// Author           : JasonTheDeveloper
// Created          : 09-07-2020
//
// Last Modified By : dannygar
// Last Modified On : 09-07-2020
// ***********************************************************************
// <copyright file="CallHandler.cs" company="Microsoft">
//     Copyright �  2020
// </copyright>
// <summary></summary>
// ***********************************************************************>

using Microsoft.Graph;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Skype.Bots.Media;
using RecordingBot.Model.Constants;
using RecordingBot.Services.Contract;
using RecordingBot.Services.ServiceSetup;
using RecordingBot.Services.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;


namespace RecordingBot.Services.Bot
{
    /// <summary>
    /// Call Handler Logic.
    /// </summary>
    public class CallHandler : HeartbeatHandler
    {
        /// <summary>
        /// Gets the call.
        /// </summary>
        /// <value>The call.</value>
        public ICall Call { get; }

        /// <summary>
        /// Gets the bot media stream.
        /// </summary>
        /// <value>The bot media stream.</value>
        public BotMediaStream BotMediaStream { get; private set; }

        /// <summary>
        /// The recording status index
        /// </summary>
        private int recordingStatusIndex = -1;

        /// <summary>
        /// The settings
        /// </summary>
        private readonly AzureSettings _settings;
        /// <summary>
        /// The event publisher
        /// </summary>
        private readonly IEventPublisher _eventPublisher;

        /// <summary>
        /// The capture
        /// </summary>
        private CaptureEvents _capture;

        /// <summary>
        /// The is disposed
        /// </summary>
        private bool _isDisposed = false;

		// hashSet of the available sockets
		private readonly HashSet<uint> availableSocketIds = new HashSet<uint>();

		// this is an LRU cache with the MSI values, we update this Cache with the dominant speaker events
		// this way we can make sure that the muliview sockets are subscribed to the active (speaking) participants
		private readonly LRUCache currentVideoSubscriptions = new LRUCache(BotConstants.NumberOfMultiviewSockets + 1);

		private readonly object subscriptionLock = new object();

		// This dictionnary helps maintaining a mapping of the sockets subscriptions
		private readonly ConcurrentDictionary<uint, uint> msiToSocketIdMapping = new ConcurrentDictionary<uint, uint>();

		/// <summary>
		/// Initializes a new instance of the <see cref="CallHandler" /> class.
		/// </summary>
		/// <param name="statefulCall">The stateful call.</param>
		/// <param name="settings">The settings.</param>
		/// <param name="eventPublisher">The event publisher.</param>
		public CallHandler(
            ICall statefulCall,
            IAzureSettings settings,
            IEventPublisher eventPublisher
        )
            : base(TimeSpan.FromMinutes(10), statefulCall?.GraphLogger)
        {
            _settings = (AzureSettings)settings;
            _eventPublisher = eventPublisher;

            this.Call = statefulCall;
            this.Call.OnUpdated += this.CallOnUpdated;
            this.Call.Participants.OnUpdated += this.ParticipantsOnUpdated;

			// subscribe to the VideoMediaReceived event on the main video socket
			this.Call.GetLocalMediaSession().VideoSockets.FirstOrDefault().VideoMediaReceived += this.OnVideoMediaReceived;

			// susbscribe to the participants updates, this will inform the bot if a particpant left/joined the conference
			this.Call.Participants.OnUpdated += this.ParticipantsOnUpdated;

			foreach (var videoSocket in this.Call.GetLocalMediaSession().VideoSockets)
			{
				this.availableSocketIds.Add((uint)videoSocket.SocketId);
			}

			this.BotMediaStream = new BotMediaStream(this.Call.GetLocalMediaSession(), this.Call.Id, this.GraphLogger, eventPublisher,  _settings);

            if (_settings.CaptureEvents)
            {
                var path = Path.Combine(Path.GetTempPath(), BotConstants.DefaultOutputFolder, _settings.EventsFolder, statefulCall.GetLocalMediaSession().MediaSessionId.ToString(), "participants");
                _capture = new CaptureEvents(path);
            }
        }

		private void OnVideoMediaReceived(object sender, VideoMediaReceivedEventArgs e)
		{
			e.Buffer.Dispose();
		}

		/// <summary>
		/// Subscribe to video or vbss sharer.
		/// if we set the flag forceSubscribe to true, the behavior is to subscribe to a video even if there is no available socket left.
		/// in that case we use the LRU cache to free socket and subscribe to the new MSI.
		/// </summary>
		/// <param name="participant">Participant sending the video or VBSS stream.</param>
		/// <param name="forceSubscribe">If forced, the least recently used video socket is released if no sockets are available.</param>
		private void SubscribeToParticipantVideo(IParticipant participant, bool forceSubscribe = true)
		{
			bool subscribeToVideo = false;
			uint socketId = uint.MaxValue;

			// filter the mediaStreams to see if the participant has a video send
			var participantSendCapableVideoStream = participant.Resource.MediaStreams.Where(x => x.MediaType == Modality.Video &&
			   (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly)).FirstOrDefault();
			if (participantSendCapableVideoStream != null)
			{
				bool updateMSICache = false;
				var msi = uint.Parse(participantSendCapableVideoStream.SourceId);
				lock (this.subscriptionLock)
				{
					if (this.currentVideoSubscriptions.Count < this.Call.GetLocalMediaSession().VideoSockets.Count)
					{
						// we want to verify if we already have a socket subscribed to the MSI
						if (!this.msiToSocketIdMapping.ContainsKey(msi))
						{
							if (this.availableSocketIds.Any())
							{
								socketId = this.availableSocketIds.Last();
								this.availableSocketIds.Remove((uint)socketId);
								subscribeToVideo = true;
							}
						}

						updateMSICache = true;
						this.GraphLogger.Info($"[{this.Call.Id}:SubscribeToParticipant(socket {socketId} available, the number of remaining sockets is {this.availableSocketIds.Count}, subscribing to the participant {participant.Id})");
					}
					else if (forceSubscribe)
					{
						// here we know that all the sockets subscribed to a video we need to update the msi cache,
						// and obtain the socketId to reuse with the new MSI
						updateMSICache = true;
						subscribeToVideo = true;
					}

					if (updateMSICache)
					{
						this.currentVideoSubscriptions.TryInsert(msi, out uint? dequeuedMSIValue);
						if (dequeuedMSIValue != null)
						{
							// Cache was updated, we need to use the new available socket to subscribe to the MSI
							this.msiToSocketIdMapping.TryRemove((uint)dequeuedMSIValue, out socketId);
						}
					}
				}

				if (subscribeToVideo && socketId != uint.MaxValue)
				{
					this.msiToSocketIdMapping.AddOrUpdate(msi, socketId, (k, v) => socketId);

					this.GraphLogger.Info($"[{this.Call.Id}:SubscribeToParticipant(subscribing to the participant {participant.Id} on socket {socketId})");
					this.BotMediaStream.Subscribe(MediaType.Video, msi, VideoResolution.HD1080p, socketId);
				}
			}

			// vbss viewer subscription
			var vbssParticipant = participant.Resource.MediaStreams.SingleOrDefault(x => x.MediaType == Modality.VideoBasedScreenSharing
			&& x.Direction == MediaDirection.SendOnly);
			if (vbssParticipant != null)
			{
				// new sharer
				this.GraphLogger.Info($"[{this.Call.Id}:SubscribeToParticipant(subscribing to the VBSS sharer {participant.Id})");
				this.BotMediaStream.Subscribe(MediaType.Vbss, uint.Parse(vbssParticipant.SourceId), VideoResolution.HD1080p, socketId);
			}
		}

		/// <summary>
		/// Unsubscribe and free up the video socket for the specified participant.
		/// </summary>
		/// <param name="participant">Particant to unsubscribe the video.</param>
		private void UnsubscribeFromParticipantVideo(IParticipant participant)
		{
			var participantSendCapableVideoStream = participant.Resource.MediaStreams.Where(x => x.MediaType == Modality.Video &&
			  (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly)).FirstOrDefault();

			if (participantSendCapableVideoStream != null)
			{
				var msi = uint.Parse(participantSendCapableVideoStream.SourceId);
				lock (this.subscriptionLock)
				{
					if (this.currentVideoSubscriptions.TryRemove(msi))
					{
						if (this.msiToSocketIdMapping.TryRemove(msi, out uint socketId))
						{
							this.BotMediaStream.Unsubscribe(MediaType.Video, socketId);
							this.availableSocketIds.Add(socketId);
						}
					}
				}
			}
		}

		/// <inheritdoc/>
		protected override Task HeartbeatAsync(ElapsedEventArgs args)
        {
            return this.Call.KeepAliveAsync();
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {

            base.Dispose(disposing);
            _isDisposed = true;
            this.Call.OnUpdated -= this.CallOnUpdated;
            this.Call.Participants.OnUpdated -= this.ParticipantsOnUpdated;

            this.BotMediaStream?.Dispose();

            // Event - Dispose of the call completed ok
            _eventPublisher.Publish("CallDisposedOK", $"Call.Id: {this.Call.Id}");
        }

        /// <summary>
        /// Called when recording status flip timer fires.
        /// </summary>
        /// <param name="source">The <see cref="ICall" /> source.</param>
        /// <param name="e">The <see cref="ElapsedEventArgs" /> instance containing the event data.</param>
        private void OnRecordingStatusFlip(ICall source, ElapsedEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                // TODO: consider rewriting the recording status checking
                var recordingStatus = new[] { RecordingStatus.Recording, RecordingStatus.NotRecording, RecordingStatus.Failed };

                var recordingIndex = this.recordingStatusIndex + 1;
                if (recordingIndex >= recordingStatus.Length)
                {
                    var recordedParticipantId = this.Call.Resource.IncomingContext.ObservedParticipantId;

                    var recordedParticipant = this.Call.Participants[recordedParticipantId];
                    await recordedParticipant.DeleteAsync().ConfigureAwait(false);
                    // Event - Recording has ended
                     _eventPublisher.Publish("CallRecordingFlip", $"Call.Id: {Call.Id} ended");
                    return;
                }

                var newStatus = recordingStatus[recordingIndex];
                try
                {
                    // Event - Log the recording status
                    var status = Enum.GetName(typeof(RecordingStatus), newStatus);
                    _eventPublisher.Publish("CallRecordingFlip", $"Call.Id: {Call.Id} status changed to {status}");

                    // NOTE: if your implementation supports stopping the recording during the call, you can call the same method above with RecordingStatus.NotRecording
                    await source
                        .UpdateRecordingStatusAsync(newStatus)
                        .ConfigureAwait(false);

                    this.recordingStatusIndex = recordingIndex;
                }
                catch (Exception exc)
                {
                    // e.g. bot joins via direct join - may not have the permissions
                    GraphLogger.Error(exc, $"Failed to flip the recording status to {newStatus}");
                    // Event - Recording status exception - failed to update 
                    _eventPublisher.Publish("CallRecordingFlip", $"Failed to flip the recording status to {newStatus}");
                }
            }).ForgetAndLogExceptionAsync(this.GraphLogger);
        }

        /// <summary>
        /// Event fired when the call has been updated.
        /// </summary>
        /// <param name="sender">The call.</param>
        /// <param name="e">The event args containing call changes.</param>
        private async void CallOnUpdated(ICall sender, ResourceEventArgs<Call> e)
        {
            GraphLogger.Info($"Call status updated to {e.NewResource.State} - {e.NewResource.ResultInfo?.Message}");
            // Event - Recording update e.g established/updated/start/ended
            _eventPublisher.Publish($"Call{e.NewResource.State}", $"Call.ID {Call.Id} Sender.Id {sender.Id} status updated to {e.NewResource.State} - {e.NewResource.ResultInfo?.Message}");

            if (e.OldResource.State != e.NewResource.State && e.NewResource.State == CallState.Established)
            {
                if (!_isDisposed)
                {
                    // Call is established. We should start receiving Audio, we can inform clients that we have started recording.
                    OnRecordingStatusFlip(sender, null);
                }
            }

            if ((e.OldResource.State == CallState.Established) && (e.NewResource.State == CallState.Terminated))
            {
                if (BotMediaStream != null)
                {
                   var aQoE = BotMediaStream.GetAudioQualityOfExperienceData();

                    if (aQoE != null)
                    {
                        if (_settings.CaptureEvents)
                            await _capture?.Append(aQoE);
                    }
                    await BotMediaStream.StopMedia();
                }

                if (_settings.CaptureEvents)
                    await _capture?.Finalise();
            }
        }

		/// <summary>
		/// Event fired when the participants collection has been updated.
		/// </summary>
		/// <param name="sender">Participants collection.</param>
		/// <param name="args">Event args containing added and removed participants.</param>
		private void ParticipantsOnUpdated(IParticipantCollection sender, CollectionEventArgs<IParticipant> args)
		{
			if (_settings.CaptureEvents)
			{
				_capture?.Append(args);
			}
			updateParticipants(args.AddedResources);
			updateParticipants(args.RemovedResources, false);

			foreach (var participant in args.AddedResources)
			{
				// todo remove the cast with the new graph implementation,
				// for now we want the bot to only subscribe to "real" participants
				var participantDetails = participant.Resource.Info.Identity.User;
				if (participantDetails != null)
				{
					// subscribe to the participant updates, this will indicate if the user started to share,
					// or added another modality
					participant.OnUpdated += this.OnParticipantUpdated;

					// the behavior here is to avoid subscribing to a new participant video if the VideoSubscription cache is full
					this.SubscribeToParticipantVideo(participant, forceSubscribe: false);
				}
			}

			foreach (var participant in args.RemovedResources)
			{
				var participantDetails = participant.Resource.Info.Identity.User;
				if (participantDetails != null)
				{
					// unsubscribe to the participant updates
					participant.OnUpdated -= this.OnParticipantUpdated;
					this.UnsubscribeFromParticipantVideo(participant);
				}
			}
		}

		/// <summary>
		/// Event fired when a participant is updated.
		/// </summary>
		/// <param name="sender">Participant object.</param>
		/// <param name="args">Event args containing the old values and the new values.</param>
		private void OnParticipantUpdated(IParticipant sender, ResourceEventArgs<Participant> args)
		{
			this.SubscribeToParticipantVideo(sender, forceSubscribe: false);
		}

		/// <summary>
		/// Creates the participant update json.
		/// </summary>
		/// <param name="participantId">The participant identifier.</param>
		/// <param name="participantDisplayName">Display name of the participant.</param>
		/// <returns>System.String.</returns>
		private string createParticipantUpdateJson(string participantId, string participantDisplayName = "")
        {
            if (participantDisplayName.Length==0)
                return "{" + String.Format($"\"Id\": \"{participantId}\"") + "}";
            else
                return "{" + String.Format($"\"Id\": \"{participantId}\", \"DisplayName\": \"{participantDisplayName}\"") + "}";
        }

        /// <summary>
        /// Updates the participant.
        /// </summary>
        /// <param name="participants">The participants.</param>
        /// <param name="participant">The participant.</param>
        /// <param name="added">if set to <c>true</c> [added].</param>
        /// <param name="participantDisplayName">Display name of the participant.</param>
        /// <returns>System.String.</returns>
        private string updateParticipant(List<IParticipant> participants, IParticipant participant, bool added, string participantDisplayName = "")
        {
            if (added)
                participants.Add(participant);
            else
                participants.Remove(participant);
            return createParticipantUpdateJson(participant.Id, participantDisplayName);
        }

        /// <summary>
        /// Updates the participants.
        /// </summary>
        /// <param name="eventArgs">The event arguments.</param>
        /// <param name="added">if set to <c>true</c> [added].</param>
        private void updateParticipants(ICollection<IParticipant> eventArgs, bool added = true)
        {
            foreach (var participant in eventArgs)
            {
                var json = string.Empty;

                // todo remove the cast with the new graph implementation,
                // for now we want the bot to only subscribe to "real" participants
                var participantDetails = participant.Resource.Info.Identity.User;

                if (participantDetails != null)
                {
                    json = updateParticipant(this.BotMediaStream.participants, participant, added, participantDetails.DisplayName);
                }
                else if (participant.Resource.Info.Identity.AdditionalData?.Count > 0)
                {
                    if (CheckParticipantIsUsable(participant))
                    {
                        json = updateParticipant(this.BotMediaStream.participants, participant, added);
                    }
                }

               if (json.Length > 0)
                    if (added)
                        _eventPublisher.Publish("CallParticipantAdded", json);
                    else
                        _eventPublisher.Publish("CallParticipantRemoved", json);
            }
        }

        /// <summary>
        /// Checks the participant is usable.
        /// </summary>
        /// <param name="p">The p.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        private bool CheckParticipantIsUsable(IParticipant p)
        {
            foreach (var i in p.Resource.Info.Identity.AdditionalData)
                if (i.Key != "applicationInstance" && i.Value is Identity)
                    return true;

            return false;
        }
    }
}
