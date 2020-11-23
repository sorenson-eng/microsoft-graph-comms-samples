﻿using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using Microsoft.Skype.Internal.Media.H264;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RecordingBot.Services.Util
{
    internal static class MediaBuffers
    {
        private const double TicksInOneMs = 10000.0;
        private const double MsInOneSec = 1000.0;
        private static readonly ConcurrentDictionary<int, List<H264Frame>> H264Frames;

        /// <summary>
        /// Creates the video buffers from the provided h264 files.
        /// </summary>
        /// <param name="currentTick">The number of ticks that represent the current date and time.</param>
        /// <param name="videoFormats">The encoded video source formats.</param>
        /// <param name="replayed">If the video frame is being replayed.</param>
        /// <param name="logger">Graph logger.</param>
        /// <returns>The newly created list of <see cref="VideoMediaBuffer"/>.</returns>
        public static List<VideoMediaBuffer> CreateVideoMediaBuffers(
            long currentTick,
            List<VideoFormat> videoFormats,
            bool replayed,
            IGraphLogger logger)
        {
            List<VideoMediaBuffer> videoMediaBuffers = new List<VideoMediaBuffer>();
            try
            {
                foreach (var videoFormat in videoFormats)
                {
                    if (H264Frames.TryGetValue(videoFormat.GetId(), out List<H264Frame> h264Frames))
                    {
                        // create the videoBuffers
                        var packetSizeInMs = (long)((MsInOneSec / videoFormat.FrameRate) * TicksInOneMs);
                        var referenceTime = currentTick;

                        if (replayed)
                        {
                            referenceTime += packetSizeInMs;
                        }

                        foreach (var h264Frame in h264Frames)
                        {
                            var frameSize = h264Frame.Size;
                            byte[] buffer = new byte[frameSize];
                            Marshal.Copy(h264Frame.Data, buffer, 0, (int)frameSize);
                            videoMediaBuffers.Add(new VideoSendBuffer(buffer, (uint)buffer.Length, videoFormat, referenceTime));
                            referenceTime += packetSizeInMs;
                        }
                    }
                    else
                    {
                        logger.Error($"h264FileReader not found for the videoFromat {videoFormat}");
                    }
                }

                logger.Info($"created {videoMediaBuffers.Count} VideoMediaBuffers");
                return videoMediaBuffers;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to create the videoMediaBuffers with exception");
            }

            return videoMediaBuffers;
        }

        /// <summary>
        /// Helper function to create the audio buffers from file.
        /// Please make sure the audio file provided is PCM16Khz and the fileSizeInSec is the correct length.
        /// </summary>
        /// <param name="currentTick">The current clock tick.</param>
        /// <param name="replayed">Whether it's replayed.</param>
        /// <param name="logger">Graph logger.</param>
        /// <returns>The newly created list of <see cref="AudioMediaBuffer"/>.</returns>
        public static List<AudioMediaBuffer> CreateAudioMediaBuffers(long currentTick, bool replayed, IGraphLogger logger)
        {
            var audioMediaBuffers = new List<AudioMediaBuffer>();
            var referenceTime = currentTick;

            // packet size of 20 ms
            var numberOfTicksInOneAudioBuffers = 20 * 10000;
            if (replayed)
            {
                referenceTime += numberOfTicksInOneAudioBuffers;
            }

            //using (FileStream fs = File.Open(Service.Instance.Configuration.AudioFileLocation, FileMode.Open))
            //{
            //    byte[] bytesToRead = new byte[640];

            //    // skipping the wav headers
            //    fs.Seek(44, SeekOrigin.Begin);
            //    while (fs.Read(bytesToRead, 0, bytesToRead.Length) >= 640)
            //    {
            //        // here we want to create buffers of 20MS with PCM 16Khz
            //        IntPtr unmanagedBuffer = Marshal.AllocHGlobal(640);
            //        Marshal.Copy(bytesToRead, 0, unmanagedBuffer, 640);
            //        var audioBuffer = new AudioSendBuffer(unmanagedBuffer, 640, AudioFormat.Pcm16K, referenceTime);
            //        audioMediaBuffers.Add(audioBuffer);
            //        referenceTime += numberOfTicksInOneAudioBuffers;
            //    }
            //}

            logger.Info($"created {audioMediaBuffers.Count} AudioMediaBuffers");
            return audioMediaBuffers;
        }

        /// <summary>
        /// Helper function get id.
        /// </summary>
        /// <param name="videoFormat">Video format.</param>
        /// <returns>The <see cref="int"/> of the video format.</returns>
        public static int GetId(this VideoFormat videoFormat)
        {
            return $"{videoFormat.VideoColorFormat}{videoFormat.Width}{videoFormat.Height}".GetHashCode();
        }
    }
}
