using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Commons.Music.Midi;

namespace Commons.Music.Midi.Player
{
	public enum PlayerState
	{
		Stopped,
		Playing,
		Paused,
		FastForward,
		Rewind,
		Loading
	}

	public interface IMidiPlayerStatus
	{
		PlayerState State { get; }
		int Tempo { get; }
		int PlayDeltaTime { get; }
		int GetTotalPlayTimeMilliseconds ();
	}

	// Player implementation. Plays a MIDI song synchronously.
	public class MidiSyncPlayer : IDisposable, IMidiPlayerStatus
	{
		public MidiSyncPlayer (SmfMusic music)
		{
			if (music == null)
				throw new ArgumentNullException ("music");

			this.music = music;
			messages = SmfTrackMerger.Merge (music).Tracks [0].Messages;
			state = PlayerState.Stopped;
		}

		public event Action Finished;

		SmfMusic music;
		IList<SmfMessage> messages;
		ManualResetEvent pause_handle = new ManualResetEvent (false);
		PlayerState state;
		bool do_pause, do_stop;

		public PlayerState State {
			get { return state; }
		}
		public int PlayDeltaTime { get; set; }
		public int Tempo {
			get { return current_tempo; }
		}
		// You can break the data at your own risk but I take performance precedence.
		public byte [] TimeSignature {
			get { return current_time_signature; }
		}
		public void SetTempoRatio (double ratio)
		{
			tempo_ratio = ratio;
		}
		public int GetTotalPlayTimeMilliseconds ()
		{
			return SmfMusic.GetTotalPlayTimeMilliseconds (messages, music.DeltaTimeSpec);
		}

		public virtual void Dispose ()
		{
			if (state != PlayerState.Stopped)
				Stop ();
			Mute ();
		}

		public void Play ()
		{
			pause_handle.Set ();
			state = PlayerState.Playing;
		}

		void AllControlReset ()
		{
			for (int i = 0; i < 16; i++)
				OnEvent (new SmfEvent ((byte) (i + 0xB0), 0x79, 0, null));
		}

		void Mute ()
		{
			for (int i = 0; i < 16; i++)
				OnEvent (new SmfEvent ((byte) (i + 0xB0), 0x78, 0, null));
		}

		public void Pause ()
		{
			do_pause = true;
			Mute ();
		}

		int event_idx = 0;

		public void PlayerLoop ()
		{
			AllControlReset ();
			{
				while (true) {
					pause_handle.WaitOne ();
					if (do_stop)
						break;
					if (do_pause) {
						pause_handle.Reset ();
						do_pause = false;
						state = PlayerState.Paused;
						continue;
					}
					if (event_idx == messages.Count)
						break;
					HandleEvent (messages [event_idx++]);
				}
				do_stop = false;
				Mute ();
				state = PlayerState.Stopped;
				if (event_idx == messages.Count)
					if (Finished != null)
						Finished ();
				event_idx = 0;
			}
		}

		int current_tempo = SmfMetaType.DefaultTempo;
		byte [] current_time_signature = new byte [4];
		double tempo_ratio = 1.0;

		int GetDeltaTimeInMilliseconds (int deltaTime)
		{
			if (music.DeltaTimeSpec < 0)
				throw new NotSupportedException ("SMPTe-basd delta time is not implemented yet");
			return (int) (current_tempo / 1000 * deltaTime / music.DeltaTimeSpec / tempo_ratio);
		}

		string ToBinHexString (byte [] bytes)
		{
			string s = "";
			foreach (byte b in bytes)
				s += String.Format ("{0:X02} ", b);
			return s;
		}

		public virtual void HandleEvent (SmfMessage m)
		{
			if (m.DeltaTime != 0) {
				var ms = GetDeltaTimeInMilliseconds (m.DeltaTime);
				Thread.Sleep (ms);
			}
			if (m.Event.StatusByte == 0xFF) {
				if (m.Event.Msb == SmfMetaType.Tempo)
					current_tempo = SmfMetaType.GetTempo (m.Event.Data);
				else if (m.Event.Msb == SmfMetaType.TimeSignature)
					Array.Copy (m.Event.Data, current_time_signature, Math.Max (4, m.Event.Data.Length));
			}

			OnEvent (m.Event);
			PlayDeltaTime += m.DeltaTime;
		}

		public MidiEventAction EventReceived;

		protected virtual void OnEvent (SmfEvent m)
		{
			if (EventReceived != null)
				EventReceived (m);
		}

		public void Stop ()
		{
			if (state != PlayerState.Stopped) {
				do_stop = true;
				if (pause_handle != null)
					pause_handle.Set ();
			}
		}
	}

	// Provides asynchronous player control.
	public class MidiPlayer : IDisposable, IMidiPlayerStatus
	{
		MidiSyncPlayer player;
		Thread sync_player_thread;

		public MidiPlayer (SmfMusic music)
		{
			player = new MidiSyncPlayer (music);
		}

		public event Action Finished {
			add { player.Finished += value; }
			remove { player.Finished -= value; }
		}

		public PlayerState State {
			get { return player.State; }
		}

		public int Tempo {
			get { return player.Tempo; }
		}
		
		public int Bpm {
			get { return (int) (60.0 / Tempo * 1000000.0); }
		}
		
		public byte [] TimeSignature {
			get { return player.TimeSignature; }
		}

		public void SetTempoRatio (double value)
		{
			player.SetTempoRatio (value);
		}

		public int PlayDeltaTime {
			get { return player.PlayDeltaTime; }
		}

		public int GetTotalPlayTimeMilliseconds ()
		{
			return player.GetTotalPlayTimeMilliseconds ();
		}

		public event MidiEventAction EventReceived {
			add { player.EventReceived += value; }
			remove { player.EventReceived -= value; }
		}

		public virtual void Dispose ()
		{
			player.Stop ();
		}

		public void StartLoop ()
		{
			ThreadStart ts = delegate { player.PlayerLoop (); };
			sync_player_thread = new Thread (ts);
			sync_player_thread.Start ();
		}

		public void PlayAsync ()
		{
			switch (State) {
			case PlayerState.Playing:
				return; // do nothing
			case PlayerState.Paused:
				player.Play ();
				return;
			case PlayerState.Stopped:
				if (sync_player_thread == null || !sync_player_thread.IsAlive)
					StartLoop ();
				player.Play ();
				return;
			}
		}

		public void PauseAsync ()
		{
			switch (State) {
			case PlayerState.Playing:
				player.Pause ();
				return;
			default: // do nothing
				return;
			}
		}
	}
}

