﻿namespace FamiStudio
{
    public class ChannelStateDpcm : ChannelState
    {
        public ChannelStateDpcm(IPlayerInterface player, int apuIdx, int channelIdx, bool pal) : base(player, apuIdx, channelIdx, pal)
        {
        }

        public override void UpdateAPU()
        {
            if (note.IsStop)
            {
                WriteRegister(NesApu.APU_SND_CHN, 0x0f);
            }
            else if (note.IsMusical && noteTriggered)
            {
                WriteRegister(NesApu.APU_SND_CHN, 0x0f);

                var mapping = FamiStudio.StaticProject.GetDPCMMapping(note.Value);
                if (mapping != null)
                {
                    var addr = FamiStudio.StaticProject.GetAddressForSample(mapping.Sample, out var len, out var dmcInitialValue) >> 6;
                    if (addr >= 0 && addr <= 0xff && len >= 0 && len <= DPCMSample.MaxSampleSize) 
                    {
                        WriteRegister(NesApu.APU_DMC_START, addr);
                        WriteRegister(NesApu.APU_DMC_LEN, len >> 4);
                        WriteRegister(NesApu.APU_DMC_FREQ, mapping.Pitch | (mapping.Loop ? 0x40 : 0x00));
                        WriteRegister(NesApu.APU_DMC_RAW, dmcInitialValue);
                        WriteRegister(NesApu.APU_SND_CHN, 0x1f);
                    }
                }
            }

            base.UpdateAPU();
        }
    }
}
