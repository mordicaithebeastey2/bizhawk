﻿using System;
using System.IO;
using System.Windows.Forms;

namespace BizHawk.MultiClient
{
	partial class MainForm
	{
		public bool ReadOnly = true;	//Global Movie Read only setting

		public void ClearFrame()
		{
			if (Global.MovieSession.Movie.IsPlaying)
			{
				Global.MovieSession.Movie.ClearFrame(Global.Emulator.Frame);
				Global.OSD.AddMessage("Scrubbed input at frame " + Global.Emulator.Frame.ToString());
			}
		}

		public void StartNewMovie(Movie m, bool record)
		{
			//If a movie is already loaded, save it before starting a new movie
			if (Global.MovieSession.Movie.IsActive)
			{
				Global.MovieSession.Movie.WriteMovie();
			}

			Global.MovieSession = new MovieSession {Movie = m};
			RewireInputChain();

			if (!record)
			{
				Global.MovieSession.Movie.LoadMovie();
				SetSyncDependentSettings();
			}

			LoadRom(Global.MainForm.CurrentlyOpenRom, true, !record);

			Global.Config.RecentMovies.Add(m.Filename);
			if (Global.MovieSession.Movie.StartsFromSavestate)
			{
				LoadStateFile(Global.MovieSession.Movie.Filename, Path.GetFileName(Global.MovieSession.Movie.Filename));
				Global.Emulator.ResetFrameCounter();
			}
			if (record)
			{
				Global.MovieSession.Movie.StartRecording();
				ReadOnly = false;
			}
			else
			{
				Global.MovieSession.Movie.StartPlayback();
			}
			SetMainformMovieInfo();
			TAStudio1.Restart();
			VirtualPadForm1.Restart();
			Global.DisplayManager.NeedsToPaint = true;
		}

		public void SetMainformMovieInfo()
		{
			if (Global.MovieSession.Movie.IsPlaying)
			{
				Text = DisplayNameForSystem(Global.Game.System) + " - " + Global.Game.Name + " - " + Path.GetFileName(Global.MovieSession.Movie.Filename);
				PlayRecordStatus.Image = Properties.Resources.Play;
				PlayRecordStatus.ToolTipText = "Movie is in playback mode";
				PlayRecordStatus.Visible = true;
			}
			else if (Global.MovieSession.Movie.IsRecording)
			{
				Text = DisplayNameForSystem(Global.Game.System) + " - " + Global.Game.Name + " - " + Path.GetFileName(Global.MovieSession.Movie.Filename);
				PlayRecordStatus.Image = Properties.Resources.RecordHS;
				PlayRecordStatus.ToolTipText = "Movie is in record mode";
				PlayRecordStatus.Visible = true;
			}
			else if (!Global.MovieSession.Movie.IsActive)
			{
				Text = DisplayNameForSystem(Global.Game.System) + " - " + Global.Game.Name;
				PlayRecordStatus.Image = Properties.Resources.Blank;
				PlayRecordStatus.ToolTipText = "No movie is active";
				PlayRecordStatus.Visible = false;
			}
		}

		public void PlayMovie()
		{
			RunLoopBlocked = true;
			new PlayMovie().ShowDialog();

			RunLoopBlocked = false;
		}

		public void RecordMovie()
		{
			RunLoopBlocked = true;
			// put any BEETA quality cores here
			if (Global.Emulator is Emulation.Consoles.Nintendo.GBA.GBA ||
				Global.Emulator is Emulation.Consoles.Sega.Genesis ||
				Global.Emulator is Emulation.Consoles.Sega.Saturn.Yabause ||
                Global.Emulator is Emulation.Consoles.Sony.PSP.PSP)
			{
				var result = MessageBox.Show
					(this, "Thanks for using Bizhawk!  The emulation core you have selected " +
					"is currently BETA-status.  We appreciate your help in testing Bizhawk. " +
					"You can record a movie on this core if you'd like to, but expect to " +
					"encounter bugs and sync problems.  Continue?", "BizHawk", MessageBoxButtons.YesNo);
				if (result != DialogResult.Yes)
					return;
			}
			new RecordMovie().ShowDialog();
			RunLoopBlocked = false;
		}

		public void PlayMovieFromBeginning()
		{
			if (Global.MovieSession.Movie.IsActive)
			{
				LoadRom(CurrentlyOpenRom, true, true);
				if (Global.MovieSession.Movie.StartsFromSavestate)
				{
					LoadStateFile(Global.MovieSession.Movie.Filename, Path.GetFileName(Global.MovieSession.Movie.Filename));
					Global.Emulator.ResetFrameCounter();
				}
				Global.MovieSession.Movie.StartPlayback();
				SetMainformMovieInfo();
				Global.OSD.AddMessage("Replaying movie file in read-only mode");
				Global.MainForm.ReadOnly = true;
			}
		}

		public void StopMovie(bool abortchanges = false)
		{
			string message = "Movie ";
			if (Global.MovieSession.Movie.IsRecording)
			{
				message += "recording ";
			}
			else if (Global.MovieSession.Movie.IsPlaying)
			{
				message += "playback ";
			}

			message += "stopped.";

			if (Global.MovieSession.Movie.IsActive)
			{
				Global.MovieSession.Movie.Stop(abortchanges);
				if (!abortchanges)
				{
					Global.OSD.AddMessage(Path.GetFileName(Global.MovieSession.Movie.Filename) + " written to disk.");
				}
				Global.OSD.AddMessage(message);
				Global.MainForm.ReadOnly = true;
				SetMainformMovieInfo();
			}
		}

		private bool HandleMovieLoadState(string path)
		{
			using (var sr = new StreamReader(path))
			{
				return HandleMovieLoadState(sr);
			}
		}

		private bool HandleMovieLoadState(StreamReader reader)
		{
			//Note, some of the situations in these IF's may be identical and could be combined but I intentionally separated it out for clarity
			if (!Global.MovieSession.Movie.IsActive)
			{
				return true;
			}

			else if (Global.MovieSession.Movie.IsRecording)
			{

				if (ReadOnly)
				{
					if (!Global.MovieSession.Movie.CheckTimeLines(reader, false))
					{
						return false;	//Timeline/GUID error
					}
					else
					{
						Global.MovieSession.Movie.WriteMovie();
						Global.MovieSession.Movie.SwitchToPlay();
						SetMainformMovieInfo();
					}
				}
				else
				{
					if (!Global.MovieSession.Movie.CheckTimeLines(reader, true))
					{
						return false;	//GUID Error
					}
					reader.BaseStream.Position = 0;
					reader.DiscardBufferedData();
					Global.MovieSession.Movie.LoadLogFromSavestateText(reader);
				}
			}

			else if (Global.MovieSession.Movie.IsPlaying && !Global.MovieSession.Movie.IsFinished)
			{
				if (ReadOnly)
				{
					if (!Global.MovieSession.Movie.CheckTimeLines(reader, false))
					{
						return false;	//Timeline/GUID error
					}
					//Frame loop automatically handles the rewinding effect based on Global.Emulator.Frame so nothing else is needed here
				}
				else
				{
					if (!Global.MovieSession.Movie.CheckTimeLines(reader, true))
					{
						return false;	//GUID Error
					}
					Global.MovieSession.Movie.SwitchToRecord();
					SetMainformMovieInfo();
					reader.BaseStream.Position = 0;
					reader.DiscardBufferedData();
					Global.MovieSession.Movie.LoadLogFromSavestateText(reader);
				}
			}
			else if (Global.MovieSession.Movie.IsFinished)
			{
				if (ReadOnly)
				{
					{
						if (!Global.MovieSession.Movie.CheckTimeLines(reader, false))
						{
							return false;	//Timeline/GUID error
						}
						else if (Global.MovieSession.Movie.IsFinished) //TimeLine check can change a movie to finished, hence the check here (not a good design)
						{
							Global.MovieSession.LatchInputFromPlayer(Global.MovieInputSourceAdapter);
						}
						else
						{
							Global.MovieSession.Movie.SwitchToPlay();
							SetMainformMovieInfo();
						}
					}
				}
				else
				{
					{
						if (!Global.MovieSession.Movie.CheckTimeLines(reader, true))
						{
							return false;	//GUID Error
						}
						else
						{
							Global.MovieSession.Movie.StartRecording();
							SetMainformMovieInfo();
							reader.BaseStream.Position = 0;
							reader.DiscardBufferedData();
							Global.MovieSession.Movie.LoadLogFromSavestateText(reader);
						}
					}
				}
			}
			return true;
		}

		private void HandleMovieSaveState(StreamWriter writer)
		{
			if (Global.MovieSession.Movie.IsActive)
			{
				Global.MovieSession.Movie.DumpLogIntoSavestateText(writer);
			}
		}

		private void HandleMovieOnFrameLoop()
		{
			if (!Global.MovieSession.Movie.IsActive)
			{
				Global.MovieSession.LatchInputFromPlayer(Global.MovieInputSourceAdapter);
			}

			else if (Global.MovieSession.Movie.IsFinished)
			{
				if (Global.Emulator.Frame < Global.MovieSession.Movie.Frames) //This scenario can happen from rewinding (suddenly we are back in the movie, so hook back up to the movie
				{
					Global.MovieSession.Movie.SwitchToPlay();
					Global.MovieSession.LatchInputFromLog();
				}
				else
				{
					Global.MovieSession.LatchInputFromPlayer(Global.MovieInputSourceAdapter);
				}
			}

			else if (Global.MovieSession.Movie.IsPlaying)
			{
				if (Global.Emulator.Frame >= Global.MovieSession.Movie.Frames)
				{
					if (TAStudio1.IsHandleCreated && !TAStudio1.IsDisposed)
					{
						Global.MovieSession.Movie.CaptureState();
						Global.MovieSession.LatchInputFromLog();
						Global.MovieSession.Movie.CommitFrame(Global.Emulator.Frame, Global.MovieOutputHardpoint);
					}
					else
					{
						Global.MovieSession.Movie.Finish();
					}
				}
				else
				{
					Global.MovieSession.Movie.CaptureState();
					Global.MovieSession.LatchInputFromLog();
					if (Global.ClientControls["ClearFrame"])
					{
						Global.MovieSession.LatchInputFromPlayer(Global.MovieInputSourceAdapter);
						ClearFrame();
					}
					else if (TAStudio1.IsHandleCreated && !TAStudio1.IsDisposed || Global.Config.MoviePlaybackPokeMode)
					{
						Global.MovieSession.LatchInputFromPlayer(Global.MovieInputSourceAdapter);
						MnemonicsGenerator mg = new MnemonicsGenerator();
						mg.SetSource( Global.MovieOutputHardpoint);
						if (!mg.IsEmpty)
						{
							Global.MovieSession.LatchInputFromPlayer(Global.MovieInputSourceAdapter);
							Global.MovieSession.Movie.PokeFrame(Global.Emulator.Frame, mg.GetControllersAsMnemonic());
						}
						else
						{
							Global.MovieSession.LatchInputFromLog();
						}
					}
				}
			}

			else if (Global.MovieSession.Movie.IsRecording)
			{
				Global.MovieSession.Movie.CaptureState();
				if (Global.MovieSession.MultiTrack.IsActive)
				{
					Global.MovieSession.LatchMultitrackPlayerInput(Global.MovieInputSourceAdapter, Global.MultitrackRewiringControllerAdapter);
				}
				else
				{
					Global.MovieSession.LatchInputFromPlayer(Global.MovieInputSourceAdapter);
				}
				//the movie session makes sure that the correct input has been read and merged to its MovieControllerAdapter;
				//this has been wired to Global.MovieOutputHardpoint in RewireInputChain
				Global.MovieSession.Movie.CommitFrame(Global.Emulator.Frame, Global.MovieOutputHardpoint);
			}
		}

		//On movie load, these need to be set based on the contents of the movie file
		private void SetSyncDependentSettings()
		{
			switch (Global.Emulator.SystemId)
			{
				case "Coleco":
					string str = Global.MovieSession.Movie.Header.GetHeaderLine(MovieHeader.SKIPBIOS);
					if (!String.IsNullOrWhiteSpace(str))
					{
						if (str.ToLower() == "true")
						{
							Global.Config.ColecoSkipBiosIntro = true;
						}
						else
						{
							Global.Config.ColecoSkipBiosIntro = false;
						}
					}
					break;
			}
		}
	}
}
