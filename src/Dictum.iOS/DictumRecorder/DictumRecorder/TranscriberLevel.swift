import AVFAudio

// The transcriber's volume. Apple's SpeechTranscriber loses words when the audio it is given is loud, so the copy of
// the audio handed to it is turned down first. Only that copy: the saved recording, the level meter and the speaker
// labels all read the audio as recorded.
//
// Why (measured 18/11/2026, counts only, the same transcriber run on a Mac over recordings decrypted in memory):
// - A loud 13 minute two-voice recording (level about 7,500 of 32,768): 1,551 words as recorded, the phone's own count.
//   Turned down: 1,717 at 30 %, 1,797 at 10 %, 1,856 at 5 %, 1,850 at 3 %. A second transcriber (Whisper) wrote 1,884.
//   Words differing from Whisper's: 542 as recorded, 480 at 10 %, 437 at 5 %, 441 at 3 %.
// - Five quieter recordings at 5 %: 983 to 983 words, 518 to 510, 94 to 93, 89 to 89, 63 to 64. At 3 % the first of
//   those fell to 971, so 5 % is the figure.
//
// simplification: one fixed turn-down for every recording, chosen from six recordings. Ceiling: it is not fitted to
// each recording's own level, and it was measured on the Mac's transcriber, not the phone's. Retire if a phone test
// shows quiet speech losing words, or when a Speech release handles loud audio itself.
enum TranscriberLevel {
    /// What the transcriber's copy is multiplied by.
    static let gain: Float = 0.05

    /// Turns `buffer` down in place. 16-bit and float samples; a buffer in any other format is left as it is.
    static func turnDown(_ buffer: AVAudioPCMBuffer, by gain: Float = gain) {
        let format = buffer.format
        let planes = format.isInterleaved ? 1 : Int(format.channelCount)
        let count = Int(buffer.frameLength) * (format.isInterleaved ? Int(format.channelCount) : 1)
        if let data = buffer.floatChannelData {
            for plane in 0..<planes {
                for i in 0..<count { data[plane][i] *= gain }
            }
        } else if let data = buffer.int16ChannelData {
            for plane in 0..<planes {
                for i in 0..<count { data[plane][i] = Int16((Float(data[plane][i]) * gain).rounded()) }
            }
        }
    }
}

#if DEBUG
/// One line per check, "PASS …" or "FAIL …". No device or model needed.
enum TranscriberLevelSelfCheck {
    static func run() -> [String] {
        var out: [String] = []
        func check<T: Equatable>(_ name: String, _ got: T, _ want: T) {
            out.append("\(got == want ? "PASS" : "FAIL") \(name): got \(got), want \(want)")
        }
        func int16(_ samples: [Int16], channels: AVAudioChannelCount = 1, interleaved: Bool = false, used: Int? = nil) -> AVAudioPCMBuffer {
            let format = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16_000, channels: channels, interleaved: interleaved)!
            let frames = samples.count / (interleaved ? Int(channels) : 1)
            let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames))!
            buffer.frameLength = AVAudioFrameCount(used ?? frames)
            for plane in 0..<(interleaved ? 1 : Int(channels)) {
                for (i, s) in samples.enumerated() { buffer.int16ChannelData![plane][i] = s }
            }
            return buffer
        }
        func read(_ buffer: AVAudioPCMBuffer, _ count: Int, plane: Int = 0) -> [Int16] {
            (0..<count).map { buffer.int16ChannelData![plane][$0] }
        }

        check("the turn-down is a turn-down", TranscriberLevel.gain > 0 && TranscriberLevel.gain < 1, true)

        let loud = int16([20_000, -20_000, 32_767, -32_768, 0, 10, -10, 9])
        TranscriberLevel.turnDown(loud, by: 0.05)
        check("16-bit: each sample multiplied and rounded", read(loud, 8), [1_000, -1_000, 1_638, -1_638, 0, 1, -1, 0])

        let standing = int16([20_000])
        TranscriberLevel.turnDown(standing)
        check("16-bit: the standing turn-down is the one used",
              read(standing, 1), [Int16((20_000 * TranscriberLevel.gain).rounded())])

        let part = int16([20_000, 20_000, 20_000, 20_000], used: 2)
        TranscriberLevel.turnDown(part, by: 0.05)
        check("16-bit: samples past the buffer's length are not touched", read(part, 4), [1_000, 1_000, 20_000, 20_000])

        let two = int16([20_000, -20_000], channels: 2)
        TranscriberLevel.turnDown(two, by: 0.05)
        check("16-bit: every channel is turned down", read(two, 2, plane: 0) + read(two, 2, plane: 1), [1_000, -1_000, 1_000, -1_000])

        let woven = int16([20_000, -20_000, 10_000, -10_000], channels: 2, interleaved: true)
        TranscriberLevel.turnDown(woven, by: 0.05)
        check("16-bit: interleaved channels are all turned down", read(woven, 4), [1_000, -1_000, 500, -500])

        let floatFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false)!
        let float = AVAudioPCMBuffer(pcmFormat: floatFormat, frameCapacity: 3)!
        float.frameLength = 3
        for (i, s) in [Float(1), -0.5, 0].enumerated() { float.floatChannelData![0][i] = s }
        TranscriberLevel.turnDown(float, by: 0.25)
        check("float: each sample multiplied", (0..<3).map { float.floatChannelData![0][$0] }, [0.25, -0.125, 0])

        let wideFormat = AVAudioFormat(commonFormat: .pcmFormatInt32, sampleRate: 16_000, channels: 1, interleaved: false)!
        let wide = AVAudioPCMBuffer(pcmFormat: wideFormat, frameCapacity: 1)!
        wide.frameLength = 1
        wide.int32ChannelData![0][0] = 20_000
        TranscriberLevel.turnDown(wide, by: 0.05)
        check("another format is left as it is", wide.int32ChannelData![0][0], 20_000)
        return out
    }
}
#endif
