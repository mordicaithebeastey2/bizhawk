﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using BizHawk.Common;
using BizHawk.Emulation.Common;
using System.ComponentModel;

namespace BizHawk.Client.Common
{
	public sealed partial class TasMovie : Bk2Movie, INotifyPropertyChanged
	{
		public const string DefaultProjectName = "default";

		private readonly Bk2MnemonicConstants Mnemonics = new Bk2MnemonicConstants();
		private readonly TasStateManager StateManager;
		private readonly TasLagLog LagLog = new TasLagLog();
		private readonly Dictionary<int, IController> InputStateCache = new Dictionary<int, IController>();
		private readonly List<string> VerificationLog = new List<string>(); // For movies that do not begin with power-on, this is the input required to get into the initial state

		public TasMovie(string path, bool startsFromSavestate = false) : base(path)
		{
			// TODO: how to call the default constructor AND the base(path) constructor?  And is base(path) calling base() ?
			StateManager = new TasStateManager(this);
			Header[HeaderKeys.MOVIEVERSION] = "BizHawk v2.0 Tasproj v1.0";
			Markers = new TasMovieMarkerList(this);
			Markers.CollectionChanged += Markers_CollectionChanged;
			Markers.Add(0, startsFromSavestate ? "Savestate" : "Power on");
		}

		public TasMovie(bool startsFromSavestate = false)
			: base()
		{
			StateManager = new TasStateManager(this);
			Header[HeaderKeys.MOVIEVERSION] = "BizHawk v2.0 Tasproj v1.0";
			Markers = new TasMovieMarkerList(this);
			Markers.CollectionChanged += Markers_CollectionChanged;
			Markers.Add(0, startsFromSavestate ? "Savestate" : "Power on");
		}

		public TasMovieMarkerList Markers { get; set; }
		public bool UseInputCache { get; set; }

		public override string PreferredExtension
		{
			get { return Extension; }
		}

		public TasStateManager TasStateManager
		{
			get { return StateManager; }
		}

		public new const string Extension = "tasproj";

		public TasMovieRecord this[int index]
		{
			get
			{
				return new TasMovieRecord
				{
					State = StateManager[index],
					LogEntry = GetInputLogEntry(index),
					Lagged = LagLog[index]
				};
			}
		}

		#region Events and Handlers 

		public event PropertyChangedEventHandler PropertyChanged;

		private bool _changes;
		public override bool Changes
		{
			get { return _changes; }
			protected set
			{
				if (_changes != value)
				{
					_changes = value;
					OnPropertyChanged("Changes");
				}
			}
		}

		// This event is Raised ony when Changes is TOGGLED.
		private void OnPropertyChanged(string propertyName)
		{
			if (PropertyChanged != null)
			{
				// Raising the event when FirstName or LastName property value changed
				PropertyChanged.Invoke(this, new PropertyChangedEventArgs(propertyName));
			}
		}

		void Markers_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
		{
			Changes = true;
		}

		#endregion

		public void ClearChanges()
		{
			Changes = false;
		}

		public void FlagChanges()
		{
			Changes = true;
		}

		public override void StartNewRecording()
		{
			ClearTasprojExtras();
			Markers.Add(0, StartsFromSavestate ? "Savestate" : "Power on");

			base.StartNewRecording();
		}

		public override void SwitchToPlay()
		{
			_mode = Moviemode.Play;
		}

		/// <summary>
		/// Removes lag log and greenzone after this frame
		/// </summary>
		/// <param name="frame">The last frame that can be valid.</param>
		private void InvalidateAfter(int frame)
		{
			LagLog.RemoveFrom(frame);
			StateManager.Invalidate(frame + 1);
			Changes = true; // TODO check if this actually removed anything before flagging changes
		}

		/// <summary>
		/// Returns the mnemonic value for boolean buttons, and actual value for floats,
		/// for a given frame and button.
		/// </summary>
		public string DisplayValue(int frame, string buttonName)
		{
			if (UseInputCache && InputStateCache.ContainsKey(frame))
			{
				return CreateDisplayValueForButton(InputStateCache[frame], buttonName);
			}

			var adapter = GetInputState(frame);

			if (UseInputCache)
			{
				InputStateCache.Add(frame, adapter);
			}

			return CreateDisplayValueForButton(adapter, buttonName);
		}

		public void FlushInputCache()
		{
			InputStateCache.Clear();
		}

		public string CreateDisplayValueForButton(IController adapter, string buttonName)
		{
			if (adapter.Type.BoolButtons.Contains(buttonName))
			{
				return adapter.IsPressed(buttonName) ?
					Mnemonics[buttonName].ToString() :
					string.Empty;
			}

			if (adapter.Type.FloatControls.Contains(buttonName))
			{
				return adapter.GetFloat(buttonName).ToString();
			}

			return "!";
		}

		public void ToggleBoolState(int frame, string buttonName)
		{
			if (frame < _log.Count)
			{
				var adapter = GetInputState(frame) as Bk2ControllerAdapter;
				adapter[buttonName] = !adapter.IsPressed(buttonName);

				var lg = LogGeneratorInstance();
				lg.SetSource(adapter);
				_log[frame] = lg.GenerateLogEntry();
				Changes = true;
				InvalidateAfter(frame);
			}
		}

		public void SetBoolState(int frame, string buttonName, bool val)
		{
			if (frame < _log.Count)
			{
				var adapter = GetInputState(frame) as Bk2ControllerAdapter;
				var old = adapter[buttonName];
				adapter[buttonName] = val;

				var lg = LogGeneratorInstance();
				lg.SetSource(adapter);
				_log[frame] = lg.GenerateLogEntry();

				if (old != val)
				{
					InvalidateAfter(frame);
					Changes = true;
				}
			}
		}

		public void SetFloatState(int frame, string buttonName, float val)
		{
			if (frame < _log.Count)
			{
				var adapter = GetInputState(frame) as Bk2ControllerAdapter;
				var old = adapter.GetFloat(buttonName);
				adapter.SetFloat(buttonName, val);

				var lg = LogGeneratorInstance();
				lg.SetSource(adapter);
				_log[frame] = lg.GenerateLogEntry();

				if (old != val)
				{
					InvalidateAfter(frame);
					Changes = true;
				}
			}
		}

		public bool BoolIsPressed(int frame, string buttonName)
		{
			return ((Bk2ControllerAdapter)GetInputState(frame))
				.IsPressed(buttonName);
		}

		public float GetFloatValue(int frame, string buttonName)
		{
			return ((Bk2ControllerAdapter)GetInputState(frame))
				.GetFloat(buttonName);
		}

		// TODO: try not to need this, or at least use GetInputState and then a log entry generator
		public string GetInputLogEntry(int frame)
		{
			if (frame < FrameCount && frame >= 0)
			{
				int getframe;

				if (LoopOffset.HasValue)
				{
					if (frame < _log.Count)
					{
						getframe = frame;
					}
					else
					{
						getframe = ((frame - LoopOffset.Value) % (_log.Count - LoopOffset.Value)) + LoopOffset.Value;
					}
				}
				else
				{
					getframe = frame;
				}

				return _log[getframe];
			}

			return string.Empty;
		}

		public void ClearGreenzone()
		{
			if (StateManager.Any())
			{
				StateManager.ClearGreenzone();
				Changes = true;
			}
		}

		public override IController GetInputState(int frame)
		{
			if (frame == Global.Emulator.Frame) // Take this opportunity to capture lag and state info if we do not have it
			{
				LagLog[Global.Emulator.Frame] = Global.Emulator.IsLagFrame;

				if (!StateManager.HasState(frame))
				{
					StateManager.Capture();
				}
			}

			return base.GetInputState(frame);
		}

		public void ClearLagLog()
		{
			LagLog.Clear();
		}

		public void DeleteLogBefore(int frame)
		{
			if (frame < _log.Count)
			{
				_log.RemoveRange(0, frame);
			}
		}

		public void CopyLog(IEnumerable<string> log)
		{
			_log.Clear();
			foreach(var entry in log)
			{
				_log.Add(entry);
			}
		}

		public void CopyVerificationLog(IEnumerable<string> log)
		{
			VerificationLog.Clear();
			foreach (var entry in log)
			{
				VerificationLog.Add(entry);
			}
		}

		public List<string> GetLogEntries()
		{
			return _log;
		}
	}
}
