﻿using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;

using RenderGraphics = FamiStudio.GLOffscreenGraphics;

namespace FamiStudio
{
    class VideoFileOscilloscope : VideoFileBase
    {
        private void BuildChannelColors(Song song, List<VideoChannelState> channels, VideoFrameMetadata[] meta, int colorMode)
        {
            Color[,] colors = new Color[meta.Length, meta[0].channelNotes.Length];

            // Get the note colors.
            for (int i = 0; i < meta.Length; i++)
            {
                var m = meta[i];

                m.channelColors = new Color[m.channelNotes.Length];

                for (int j = 0; j < channels.Count; j++)
                {
                    var note = m.channelNotes[channels[j].songChannelIndex];

                    if (note != null && note.IsMusical)
                    {
                        var color = Theme.LightGreyFillColor1;

                        if (colorMode == OscilloscopeColorType.Channel)
                        {
                            var channel = song.Channels[channels[j].songChannelIndex];
                            for (int p = 0; p < channel.PatternInstances.Length; p++)
                            {
                                if (channel.PatternInstances[p] != null)
                                {
                                    color = channel.PatternInstances[p].Color;
                                    break;
                                }
                            }
                        }
                        else if (colorMode != OscilloscopeColorType.None)
                        {
                            if (channels[j].channel.Type == ChannelType.Dpcm)
                            {
                                if (colorMode == OscilloscopeColorType.InstrumentsAndSamples)
                                {
                                    var mapping = channels[j].channel.Song.Project.GetDPCMMapping(note.Value);
                                    if (mapping != null)
                                        color = mapping.Sample.Color;
                                }
                            }
                            else
                            {
                                if (note.Instrument != null && (colorMode == OscilloscopeColorType.Instruments || colorMode == OscilloscopeColorType.InstrumentsAndSamples))
                                {
                                    color = note.Instrument.Color;
                                }
                            }
                        }

                        colors[i, j] = color;
                    }
                }
            }

            // Extend any color until we hit another one.
            for (int i = 0; i < colors.GetLength(0) - 1; i++)
            {
                for (int j = 0; j < colors.GetLength(1); j++)
                {
                    if (colors[i, j].A != 0)
                    {
                        if (colors[i + 1, j].A == 0)
                            colors[i + 1, j] = colors[i, j];
                    }
                    else
                    {
                        colors[i, j] = Theme.LightGreyFillColor1;
                    }
                }
            }

            const int ColorBlendTime = 5;

            // Blend the colors.
            for (int i = 0; i < meta.Length; i++)
            {
                var m = meta[i];

                for (int j = 0; j < m.channelColors.Length; j++)
                {
                    int avgR = 0;
                    int avgG = 0;
                    int avgB = 0;
                    int count = 0;

                    for (int k = i; k < i + ColorBlendTime && k < meta.Length; k++)
                    {
                        avgR += colors[k, j].R;
                        avgG += colors[k, j].G;
                        avgB += colors[k, j].B;
                        count++;
                    }

                    m.channelColors[j] = Color.FromArgb(avgR / count, avgG / count, avgB / count);
                }
            }
        }

        public bool Save(Project originalProject, int songId, int loopCount, int colorMode, int numColumns, int lineThickness, string filename, int resX, int resY, bool halfFrameRate, int channelMask, int audioBitRate, int videoBitRate, bool stereo, float[] pan)
        {
            if (!Initialize(channelMask, loopCount))
                return false;

            videoResX = resX;
            videoResY = resY;

            var project = originalProject.DeepClone();
            var song = project.GetSong(songId);

            ExtendSongForLooping(song, loopCount);

            // Save audio to temporary file.
            Log.LogMessage(LogSeverity.Info, "Exporting audio...");

            var tempFolder = Utils.GetTemporaryDiretory();
            var tempAudioFile = Path.Combine(tempFolder, "temp.wav");

            AudioExportUtils.Save(song, tempAudioFile, SampleRate, 1, -1, channelMask, false, false, stereo, pan, (samples, samplesChannels, fn) => { WaveFile.Save(samples, fn, SampleRate, samplesChannels); });

            // Start encoder, must be done before any GL calls on Android.
            GetFrameRateInfo(song.Project, halfFrameRate, out var frameRateNumer, out var frameRateDenom);

            if (!videoEncoder.BeginEncoding(videoResX, videoResY, frameRateNumer, frameRateDenom, videoBitRate, audioBitRate, tempAudioFile, filename))
            {
                Log.LogMessage(LogSeverity.Error, "Error starting video encoder, aborting.");
                return false;
            }

            Log.LogMessage(LogSeverity.Info, "Initializing channels...");

            var numChannels = Utils.NumberOfSetBits(channelMask);
            var longestChannelName = 0.0f;
            var videoGraphics = RenderGraphics.Create(videoResX, videoResY, true);

            if (videoGraphics == null)
            {
                Log.LogMessage(LogSeverity.Error, "Error initializing off-screen graphics, aborting.");
                return false;
            }

            var themeResources = new ThemeRenderResources(videoGraphics);
            var bmpWatermark = videoGraphics.CreateBitmapFromResource("VideoWatermark");

            // Generate WAV data for each individual channel for the oscilloscope.
            var channelStates = new List<VideoChannelState>();
            var maxAbsSample = 0;

            for (int i = 0, channelIndex = 0; i < song.Channels.Length; i++)
            {
                if ((channelMask & (1 << i)) == 0)
                    continue;

                var pattern = song.Channels[i].PatternInstances[0];
                var state = new VideoChannelState();

                state.videoChannelIndex = channelIndex;
                state.songChannelIndex = i;
                state.channel = song.Channels[i];
                state.channelText = state.channel.NameWithExpansion;
                state.wav = new WavPlayer(SampleRate, 1, 1 << i).GetSongSamples(song, song.Project.PalMode, -1);

                channelStates.Add(state);
                channelIndex++;

                // Find maximum absolute value to rescale the waveform.
                foreach (int s in state.wav)
                    maxAbsSample = Math.Max(maxAbsSample, Math.Abs(s));

                // Measure the longest text.
                longestChannelName = Math.Max(longestChannelName, videoGraphics.MeasureString(state.channelText, themeResources.FontVeryLarge));

                Log.ReportProgress(0.0f);
            }

            numColumns = Math.Min(numColumns, channelStates.Count);

            var numRows = (int)Math.Ceiling(channelStates.Count / (float)numColumns);

            var channelResXFloat = videoResX / (float)numColumns;
            var channelResYFloat = videoResY / (float)numRows;

            var channelResX = (int)channelResXFloat;
            var channelResY = (int)channelResYFloat;

            // Tweak some cosmetic stuff that depends on resolution.
            var smallChannelText = channelResY < 128;
            var bmpSuffix = smallChannelText ? "" : "@2x";
            var font = lineThickness > 1 ?
                (smallChannelText ? themeResources.FontMediumBold : themeResources.FontVeryLargeBold) : 
                (smallChannelText ? themeResources.FontMedium     : themeResources.FontVeryLarge);
            var textOffsetY = smallChannelText ? 1 : 4;
            var channelLineWidth = resY >= 720 ? 5 : 3;

            foreach (var s in channelStates)
                s.bmpIcon = videoGraphics.CreateBitmapFromResource(ChannelType.Icons[s.channel.Type] + bmpSuffix);

            var gradientSizeY = channelResY / 2;
            var gradientBrush = videoGraphics.CreateVerticalGradientBrush(0, gradientSizeY, Color.Black, Color.FromArgb(0, Color.Black));

            // Generate the metadata for the video so we know what's happening at every frame
            var metadata = new VideoMetadataPlayer(SampleRate, 1).GetVideoMetadata(song, song.Project.PalMode, -1);

            var oscScale = maxAbsSample != 0 ? short.MaxValue / (float)maxAbsSample : 1.0f;
            var oscLookback = (metadata[1].wavOffset - metadata[0].wavOffset) / 2;
            var oscWindowSize = (int)Math.Round(SampleRate * OscilloscopeWindowSize);

            BuildChannelColors(song, channelStates, metadata, colorMode);

            var videoImage = new byte[videoResY * videoResX * 4];
            var oscilloscope = new float[oscWindowSize, 2];
            var success = true;

#if !DEBUG
            try
#endif
            {
                // Generate each of the video frames.
                for (int f = 0; f < metadata.Length; f++)
                {
                    if (Log.ShouldAbortOperation)
                    {
                        success = false;
                        break;
                    }

                    if ((f % 100) == 0)
                        Log.LogMessage(LogSeverity.Info, $"Rendering frame {f} / {metadata.Length}");

                    Log.ReportProgress(f / (float)(metadata.Length - 1));

                    if (halfFrameRate && (f & 1) != 0)
                        continue;

                    var frame = metadata[f];

                    videoGraphics.BeginDrawFrame();
                    videoGraphics.BeginDrawControl(new Rectangle(0, 0, videoResX, videoResY), videoResY);
                    videoGraphics.Clear(Theme.DarkGreyLineColor2);

                    var cmd = videoGraphics.CreateCommandList();

                    // Draw gradients.
                    for (int i = 0; i < numRows; i++)
                    {
                        cmd.PushTranslation(0, i * channelResY);
                        cmd.FillRectangle(0, 0, videoResX, channelResY, gradientBrush);
                        cmd.PopTransform();
                    }

                    // Channel names + oscilloscope
                    for (int i = 0; i < channelStates.Count; i++)
                    {
                        var s = channelStates[i];

                        var channelX = i % numColumns;
                        var channelY = i / numColumns;

                        var channelPosX0 = (channelX + 0) * channelResX;
                        var channelPosX1 = (channelX + 1) * channelResX;
                        var channelPosY0 = (channelY + 0) * channelResY;
                        var channelPosY1 = (channelY + 1) * channelResY;

                        // Intentionally flipping min/max Y since D3D is upside down compared to how we display waves typically.
                        GenerateOscilloscope(s.wav, frame.wavOffset, oscWindowSize, oscLookback, oscScale, channelPosX0, channelPosY1, channelPosX1, channelPosY0, oscilloscope);

                        var brush = videoGraphics.GetSolidBrush(frame.channelColors[i]);

                        cmd.DrawGeometry(oscilloscope, brush, lineThickness, true);

                        var channelIconPosX = channelPosX0 + s.bmpIcon.Size.Width  / 2;
                        var channelIconPosY = channelPosY0 + s.bmpIcon.Size.Height / 2;

                        cmd.FillAndDrawRectangle(channelIconPosX, channelIconPosY, channelIconPosX + s.bmpIcon.Size.Width - 1, channelIconPosY + s.bmpIcon.Size.Height - 1, themeResources.DarkGreyLineBrush2, themeResources.LightGreyFillBrush1);
                        cmd.DrawBitmap(s.bmpIcon, channelIconPosX, channelIconPosY, 1, Theme.LightGreyFillColor1);
                        cmd.DrawText(s.channelText, font, channelIconPosX + s.bmpIcon.Size.Width + ChannelIconTextSpacing, channelIconPosY + textOffsetY, themeResources.LightGreyFillBrush1); 
                    }

                    // Grid lines
                    for (int i = 1; i < numRows; i++)
                        cmd.DrawLine(0, i * channelResY, videoResX, i * channelResY, themeResources.BlackBrush, channelLineWidth); 
                    for (int i = 1; i < numColumns; i++)
                        cmd.DrawLine(i * channelResX, 0, i * channelResX, videoResY, themeResources.BlackBrush, channelLineWidth);

                    // Watermark.
                    cmd.DrawBitmap(bmpWatermark, videoResX - bmpWatermark.Size.Width, videoResY - bmpWatermark.Size.Height);
                    videoGraphics.DrawCommandList(cmd);
                    videoGraphics.EndDrawControl();
                    videoGraphics.EndDrawFrame();

                    // Readback
                    videoGraphics.GetBitmap(videoImage);
                    
                    // Send to encoder.
                    videoEncoder.AddFrame(videoImage);
                }

                videoEncoder.EndEncoding(!success);

                File.Delete(tempAudioFile);
            }
#if !DEBUG
            catch (Exception e)
            {
                Log.LogMessage(LogSeverity.Error, "Error exporting video.");
                Log.LogMessage(LogSeverity.Error, e.Message);
            }
            finally
#endif
            {
                foreach (var c in channelStates)
                    c.bmpIcon.Dispose();

                themeResources.Dispose();
                bmpWatermark.Dispose();
                gradientBrush.Dispose();
                videoGraphics.Dispose();
            }

            return success;
        }
    }

    static class OscilloscopeColorType
    {
        public const int None = 0;
        public const int Instruments = 1;
        public const int InstrumentsAndSamples = 2;
        public const int Channel = 3;

        public static readonly string[] Names =
        {
            "None",
            "Instruments",
            "Instruments and Samples",
            "Channel (First pattern color)"
        };

        public static int GetIndexForName(string str)
        {
            return Array.IndexOf(Names, str);
        }
    }
}
