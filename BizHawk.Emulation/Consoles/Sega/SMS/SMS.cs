﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BizHawk.Emulation.CPUs.Z80;
using BizHawk.Emulation.Sound;

/*****************************************************

  TODO: 
  + HCounter
  + Try to clean up the organization of the source code. 
  + Fix remaining broken games

**********************************************************/

namespace BizHawk.Emulation.Consoles.Sega
{
    public sealed partial class SMS : IEmulator
    {
        // Constants
        public const int BankSize = 16384;

        // ROM
        public byte[] RomData;
        public byte RomBank0, RomBank1, RomBank2;
        public byte RomBanks;
        public string[] Options;

        // SaveRAM
        public byte[] SaveRAM = new byte[BankSize*2];
        public byte SaveRamBank;

        public byte[] SaveRam { get { return SaveRAM; } }
        public bool SaveRamModified { get; set; }

        // Machine resources
        public Z80A Cpu;
        public byte[] SystemRam;
        public VDP Vdp;
        public SN76489 PSG;
        public YM2413 YM2413;
        public SoundMixer SoundMixer;
        public bool IsGameGear = false;

        public bool HasYM2413 = false;
        public int IPeriod = 228;

        public int Frame { get; set; }
        private byte Port01 = 0xFF;
        private byte Port02 = 0xFF;
        private byte Port3E = 0xAF;
        private byte Port3F = 0xFF;

        public DisplayType DisplayType { get; set; }
        public bool DeterministicEmulation { get; set; }

        public void Init()
        {
            if (Controller == null)
                Controller = NullController.GetNullController();

            Cpu = new Z80A();
            Cpu.RegisterSP = 0xDFF0;
            Cpu.ReadHardware = ReadPort;
            Cpu.WriteHardware = WritePort;

            Vdp = new VDP(Cpu, IsGameGear ? VdpMode.GameGear : VdpMode.SMS, DisplayType);
            PSG = new SN76489();
            YM2413 = new YM2413();
            SoundMixer = new SoundMixer(YM2413, PSG);
            if (HasYM2413 && Options.Contains("WhenFMDisablePSG"))
                SoundMixer.DisableSource(PSG);
            ActiveSoundProvider = HasYM2413 ? (ISoundProvider) SoundMixer : PSG;

            SystemRam = new byte[0x2000];
            if (Options.Contains("CMMapper") == false)
                InitSegaMapper();
            else
                InitCodeMastersMapper();

            if (Options.Contains("BIOS"))
            {
                Port3E = 0xF7; // Disable cartridge, enable BIOS rom
                InitBiosMapper();
            }
            SetupMemoryDomains();
        }

        public void LoadGame(IGame game)
        {
            RomData = game.GetRomData();
            if (RomData.Length % BankSize != 0)
                Array.Resize(ref RomData, ((RomData.Length/BankSize) + 1)*BankSize);
            RomBanks = (byte)(RomData.Length/BankSize);
            Options = game.GetOptions();
            DisplayType = DisplayType.NTSC;
            foreach (string option in Options)
            {
                var args = option.Split('=');
                if (option == "FM") HasYM2413 = true;
                else if (args[0] == "IPeriod") IPeriod = int.Parse(args[1]);
                else if (args[0] == "Japan") Region = "Japan";
                else if (args[0] == "PAL") DisplayType = DisplayType.PAL;
            }

            Init();
        }

        public byte ReadPort(ushort port)
        {
            switch (port & 0xFF)
            {
                case 0x00: return ReadPort0();
                case 0x01: return Port01;
                case 0x02: return Port02;
                case 0x03: return 0x00;
                case 0x04: return 0xFF;
                case 0x05: return 0x00;
                case 0x06: return 0xFF;
                case 0x3E: return Port3E;
                case 0x7E: return Vdp.ReadVLineCounter();
                case 0x7F: break; // hline counter TODO
                case 0xBE: return Vdp.ReadData();
                case 0xBF: return Vdp.ReadVdpStatus();
                case 0xC0:
                case 0xDC: return ReadControls1();
                case 0xC1: 
                case 0xDD: return ReadControls2();
                case 0xF2: return HasYM2413 ? YM2413.DetectionValue : (byte) 0xFF;
            }
            return 0xFF;
        }

        public void WritePort(ushort port, byte value)
        {
            switch (port & 0xFF)
            {
                case 0x01: Port01 = value; break;
                case 0x02: Port02 = value; break;
                case 0x06: PSG.StereoPanning = value; break;
                case 0x3E: Port3E = value; break;
                case 0x3F: Port3F = value; break;
                case 0x7E:
                case 0x7F: PSG.WritePsgData(value, Cpu.TotalExecutedCycles); break;
                case 0xBE: Vdp.WriteVdpData(value); break;
                case 0xBD:
                case 0xBF: Vdp.WriteVdpControl(value); break;
                case 0xF0: if (HasYM2413) YM2413.RegisterLatch = value; break;
                case 0xF1: if (HasYM2413) YM2413.Write(value); break;
                case 0xF2: if (HasYM2413) YM2413.DetectionValue = value; break;
            }
        }

        public void FrameAdvance(bool render)
        {
            Controller.FrameNumber = Frame++;
            PSG.BeginFrame(Cpu.TotalExecutedCycles);

            if (IsGameGear == false)
                Cpu.NonMaskableInterrupt = Controller["Pause"];

            Vdp.ExecFrame(render);
            PSG.EndFrame(Cpu.TotalExecutedCycles);
        }

        public void SaveStateText(TextWriter writer)
        {
            writer.WriteLine("[SMS]\n");
            Cpu.SaveStateText(writer);
            PSG.SaveStateText(writer);
            Vdp.SaveStateText(writer);

            writer.WriteLine("Frame {0}", Frame);
            writer.WriteLine("Bank0 {0}", RomBank0);
            writer.WriteLine("Bank1 {0}", RomBank1);
            writer.WriteLine("Bank2 {0}", RomBank2);
            writer.Write("RAM ");
            SystemRam.SaveAsHex(writer);
            writer.WriteLine("Port01 {0:X2}", Port01);
            writer.WriteLine("Port02 {0:X2}", Port02);
            writer.WriteLine("Port3F {0:X2}", Port3F);
            int SaveRamLen = Util.SaveRamBytesUsed(SaveRAM);
            if (SaveRamLen > 0)
            {
                writer.Write("SaveRAM ");
                SaveRAM.SaveAsHex(writer, SaveRamLen);
            }
            if (HasYM2413)
            {
                writer.Write("FMRegs " );
                YM2413.opll.reg.SaveAsHex(writer);
            }
            writer.WriteLine("[/SMS]");
        }

        public void LoadStateText(TextReader reader)
        {
            while (true)
            {
                string[] args = reader.ReadLine().Split(' ');
                if (args[0].Trim() == "") continue;
                if (args[0] == "[SMS]") continue;
                if (args[0] == "[/SMS]") break;
                if (args[0] == "Bank0")
                    RomBank0 = byte.Parse(args[1]);
                else if (args[0] == "Bank1")
                    RomBank1 = byte.Parse(args[1]);
                else if (args[0] == "Bank2")
                    RomBank2 = byte.Parse(args[1]);
                else if (args[0] == "Frame")
                    Frame = int.Parse(args[1]);
                else if (args[0] == "RAM")
                    SystemRam.ReadFromHex(args[1]);
                else if (args[0] == "SaveRAM")
                {
                    for (int i = 0; i < SaveRAM.Length; i++) SaveRAM[i] = 0;
                    SaveRAM.ReadFromHex(args[1]);
                }
                else if (args[0] == "FMRegs")
                {
                    byte[] regs = new byte[YM2413.opll.reg.Length];
                    regs.ReadFromHex(args[1]);
                    for (byte i=0; i<regs.Length; i++)
                        YM2413.Write(i, regs[i]);
                }
                else if (args[0] == "Port01")
                    Port01 = byte.Parse(args[1], NumberStyles.HexNumber);
                else if (args[0] == "Port02")
                    Port02 = byte.Parse(args[1], NumberStyles.HexNumber);
                else if (args[0] == "Port3F")
                    Port3F = byte.Parse(args[1], NumberStyles.HexNumber);
                else if (args[0] == "[Z80]")
                    Cpu.LoadStateText(reader);
                else if (args[0] == "[PSG]")
                    PSG.LoadStateText(reader);
                else if (args[0] == "[VDP]")
                    Vdp.LoadStateText(reader);
                else
                    Console.WriteLine("Skipping unrecognized identifier " + args[0]);
            }
        }

        public byte[] SaveStateBinary()
        {
            var buf = new byte[24802 + 16384 + 16384];
            var stream = new MemoryStream(buf);
            var writer = new BinaryWriter(stream);
            SaveStateBinary(writer);
            writer.Close();
            return buf;
        }

        public void SaveStateBinary(BinaryWriter writer)
        {
            Cpu.SaveStateBinary(writer);
            PSG.SaveStateBinary(writer);
            Vdp.SaveStateBinary(writer);

            writer.Write(Frame);
            writer.Write(RomBank0);
            writer.Write(RomBank1);
            writer.Write(RomBank2);
            writer.Write(SystemRam);
            writer.Write(SaveRAM);
            writer.Write(Port01); 
            writer.Write(Port02);
            writer.Write(Port3F);
            writer.Write(YM2413.opll.reg);
        }

        public void LoadStateBinary(BinaryReader reader)
        {
            Cpu.LoadStateBinary(reader);
            PSG.LoadStateBinary(reader);
            Vdp.LoadStateBinary(reader);

            Frame = reader.ReadInt32();
            RomBank0 = reader.ReadByte();
            RomBank1 = reader.ReadByte();
            RomBank2 = reader.ReadByte();
            SystemRam = reader.ReadBytes(SystemRam.Length);
            reader.Read(SaveRAM, 0, SaveRAM.Length);
            Port01 = reader.ReadByte();
            Port02 = reader.ReadByte();
            Port3F = reader.ReadByte();
            if (HasYM2413)
            {
                byte[] regs = new byte[YM2413.opll.reg.Length];
                reader.Read(regs, 0, regs.Length);
                for (byte i = 0; i < regs.Length; i++)
                    YM2413.Write(i, regs[i]);
            }
        }

        public IVideoProvider VideoProvider { get { return Vdp; } }

        private ISoundProvider ActiveSoundProvider;
        public ISoundProvider SoundProvider { get { return ActiveSoundProvider; } }

        private string region = "Export";
        public string Region
        {
            get { return region; }
            set
            {
                if (value.NotIn(validRegions))
                    throw new Exception("Passed value "+value+" is not a valid region!");
                region = value;
            }
        }

        private readonly string[] validRegions = {"Export", "Japan"};

        private IList<MemoryDomain> memoryDomains;

        private void SetupMemoryDomains()
        {
            var domains = new List<MemoryDomain>(3);
            var MainMemoryDomain = new MemoryDomain("Main RAM", SystemRam.Length, Endian.Little, 
                addr => SystemRam[addr & RamSizeMask], 
                (addr, value) => SystemRam[addr & RamSizeMask] = value);
            var VRamDomain = new MemoryDomain("Video RAM", Vdp.VRAM.Length, Endian.Little, 
                addr => Vdp.VRAM[addr & 0x3FFF], 
                (addr, value) => Vdp.VRAM[addr & 0x3FFF] = value);
            var SaveRamDomain = new MemoryDomain("Save RAM", SaveRAM.Length, Endian.Little, 
                addr => SaveRAM[addr%SaveRAM.Length], 
                (addr, value) => { SaveRAM[addr%SaveRAM.Length]=value; SaveRamModified=true;});

            domains.Add(MainMemoryDomain);
            domains.Add(VRamDomain);
            domains.Add(SaveRamDomain);
            memoryDomains = domains.AsReadOnly();
        }

        public IList<MemoryDomain> MemoryDomains { get { return memoryDomains; } }
        public MemoryDomain MainMemory { get { return memoryDomains[0]; } }
    }
}