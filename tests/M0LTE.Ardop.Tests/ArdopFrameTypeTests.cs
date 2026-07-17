using M0LTE.Ardop;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// Pins the frame-type table — codes, names, geometry — against ardopcf's
/// <c>FrameInfo</c>/<c>strFrameType</c> (ARDOPC.c) and the frame-classification
/// helpers, plus the codec's session-ID and station-ID encodings.
/// </summary>
public class ArdopFrameTypeTests
{
    [Theory]
    // code, name, carriers, modulation, baud, data/car, RS/car
    [InlineData(0x48, "4FSK.200.50S.E", 1, ArdopModulation.Fsk4, 50, 16, 4)]
    [InlineData(0x49, "4FSK.200.50S.O", 1, ArdopModulation.Fsk4, 50, 16, 4)]
    [InlineData(0x4A, "4FSK.500.100.E", 1, ArdopModulation.Fsk4, 100, 64, 16)]
    [InlineData(0x4B, "4FSK.500.100.O", 1, ArdopModulation.Fsk4, 100, 64, 16)]
    [InlineData(0x4C, "4FSK.500.100S.E", 1, ArdopModulation.Fsk4, 100, 32, 8)]
    [InlineData(0x4D, "4FSK.500.100S.O", 1, ArdopModulation.Fsk4, 100, 32, 8)]
    [InlineData(0x7A, "4FSK.2000.600.E", 1, ArdopModulation.Fsk4, 600, 600, 150)]
    [InlineData(0x7B, "4FSK.2000.600.O", 1, ArdopModulation.Fsk4, 600, 600, 150)]
    [InlineData(0x7C, "4FSK.2000.600S.E", 1, ArdopModulation.Fsk4, 600, 200, 50)]
    [InlineData(0x7D, "4FSK.2000.600S.O", 1, ArdopModulation.Fsk4, 600, 200, 50)]
    [InlineData(0x40, "4PSK.200.100.E", 1, ArdopModulation.Psk4, 100, 64, 32)]
    [InlineData(0x42, "4PSK.200.100S.E", 1, ArdopModulation.Psk4, 100, 16, 8)]
    [InlineData(0x44, "8PSK.200.100.E", 1, ArdopModulation.Psk8, 100, 108, 36)]
    [InlineData(0x46, "16QAM.200.100.E", 1, ArdopModulation.Qam16, 100, 128, 64)]
    [InlineData(0x50, "4PSK.500.100.E", 2, ArdopModulation.Psk4, 100, 64, 32)]
    [InlineData(0x52, "8PSK.500.100.E", 2, ArdopModulation.Psk8, 100, 108, 36)]
    [InlineData(0x54, "16QAM.500.100.E", 2, ArdopModulation.Qam16, 100, 128, 64)]
    [InlineData(0x60, "4PSK.1000.100.E", 4, ArdopModulation.Psk4, 100, 64, 32)]
    [InlineData(0x62, "8PSK.1000.100.E", 4, ArdopModulation.Psk8, 100, 108, 36)]
    [InlineData(0x64, "16QAM.1000.100.E", 4, ArdopModulation.Qam16, 100, 128, 64)]
    [InlineData(0x70, "4PSK.2000.100.E", 8, ArdopModulation.Psk4, 100, 64, 32)]
    [InlineData(0x72, "8PSK.2000.100.E", 8, ArdopModulation.Psk8, 100, 108, 36)]
    [InlineData(0x74, "16QAM.2000.100.E", 8, ArdopModulation.Qam16, 100, 128, 64)]
    public void Data_Frame_Geometry_Matches_FrameInfo(
        byte code, string name, int carriers, ArdopModulation modulation, int baud, int dataLen, int rsLen)
    {
        var info = ArdopFrameInfo.Get(code);
        info.Name.Should().Be(name);
        info.CarrierCount.Should().Be(carriers);
        info.Modulation.Should().Be(modulation);
        info.Baud.Should().Be(baud);
        info.DataLength.Should().Be(dataLen);
        info.RsLength.Should().Be(rsLen);
        info.IsOdd.Should().Be((code & 1) != 0);
        ArdopFrameType.IsData(code).Should().BeTrue();
        ArdopFrameType.IsShortControl(code).Should().BeFalse();
    }

    [Fact]
    public void Exactly_36_Data_Frame_Codes_Exist()
    {
        // 18 named data modes, even/odd paired (strAllDataModes, ARDOPC.c:289).
        Enumerable.Range(0, 256).Count(t => ArdopFrameType.IsData((byte)t)).Should().Be(36);
    }

    [Fact]
    public void The_Valid_Type_Candidate_Set_Matches_Ardopcf()
    {
        // bytValidFrameTypesALL (ARDOPC.c:226): 32 NAKs + 7 control/ID + 8 ConReq +
        // 4 ConAck + PingAck + Ping + 36 data + 32 ACKs = 121 candidates.
        ArdopFrameType.ValidTypesAll.Length.Should().Be(121);
    }

    [Theory]
    [InlineData(ArdopFrameType.Break)]
    [InlineData(ArdopFrameType.Idle)]
    [InlineData(ArdopFrameType.Disc)]
    [InlineData(ArdopFrameType.End)]
    [InlineData(ArdopFrameType.ConRejBusy)]
    [InlineData(ArdopFrameType.ConRejBw)]
    [InlineData(0x00)]
    [InlineData(0x1F)]
    [InlineData(0xE0)]
    [InlineData(0xFF)]
    public void Short_Control_Frames_Are_Classified(byte type)
    {
        ArdopFrameType.IsShortControl(type).Should().BeTrue();
        ArdopFrameType.IsData(type).Should().BeFalse();
        ArdopFrameInfo.Get(type).DataLength.Should().Be(0);
    }

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x2F)]
    [InlineData(0x3F)]
    [InlineData(0x4E)]
    [InlineData(0x56)]
    [InlineData(0x66)]
    [InlineData(0x76)]
    [InlineData(0x7E)]
    public void Unassigned_Codes_Have_No_Frame(byte type)
    {
        ArdopFrameInfo.TryGet(type, out _).Should().BeFalse();
        ArdopFrameType.Name(type).Should().BeEmpty();
    }

    [Fact]
    public void Session_Id_Is_Crc8_With_The_0xFF_Remap()
    {
        // Vector pinned by the reference harness: CRC-8("M7TFFGB7RDG") = 0x1D.
        ArdopCrc.SessionId("M7TFF", "GB7RDG").Should().Be(0x1D);

        // 0xFF remaps to 0 — reserved for unconnected/FEC (GenerateSessionID,
        // ARQ.c:507). This caller/target pair CRCs to 0xFF (found by search).
        ArdopCrc.Crc8(System.Text.Encoding.ASCII.GetBytes("M7TFHGB7EAG-1")).Should().Be(0xFF);
        ArdopCrc.SessionId("M7TFH", "GB7EAG-1").Should().Be(0);
    }

    [Fact]
    public void Station_Id_Round_Trips_Through_Packed6()
    {
        Span<byte> wire = stackalloc byte[6];
        foreach (string call in new[] { "M7TFF", "M7TFF-3", "GB7RDG-15", "AB1CDEF-C", "N0CALL-10" })
        {
            ArdopStationId.TryParse(call, out var stationId).Should().BeTrue(call);
            stationId.ToBytes(wire);
            ArdopStationId.TryFromBytes(wire, out var roundTripped).Should().BeTrue(call);
            roundTripped.ToString().Should().Be(call);
        }
    }

    [Theory]
    [InlineData("A")]        // too short
    [InlineData("ABCDEFGH")] // too long
    [InlineData("M7TFF-16")] // SSID out of range
    [InlineData("")]
    public void Invalid_Station_Ids_Are_Rejected(string text)
    {
        ArdopStationId.TryParse(text, out _).Should().BeFalse();
    }
}
