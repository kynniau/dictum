import Combine
import SwiftUI

// Small pieces of the design language that sit beside Theme.swift: the speaker badge for transcript lines and the
// level-history waveform for the recording screen.

// MARK: - Speaker badge

/// A 32 pt circle with a speaker's initial (or number) on a soft tint. Decorative: the line it sits on carries the
/// speaker's name in words, so VoiceOver skips the badge. Every tint pairs a light fill with a dark text shade that
/// passes 4.5:1 on it (each pair is 6.3:1 or more), in both appearances.
struct SpeakerBadge: View {
    let speaker: Int
    /// The name given to this speaker, if any; its first letter is shown, else the speaker's number.
    var name: String?

    @ScaledMetric(relativeTo: .footnote) private var size: CGFloat = 32

    // simplification: six fixed tints, chosen by speaker number. Ceiling: a seventh speaker repeats the first tint (the
    // letter or number still tells them apart). Retire if meetings routinely carry more than six speakers.
    private static let tints: [(fill: Color, ink: Color)] = [
        (Color(red: 0.863, green: 0.902, blue: 0.984), Color(red: 0.090, green: 0.227, blue: 0.620)), // #DCE6FB / #173A9E
        (Color(red: 0.890, green: 0.906, blue: 0.933), Color(red: 0.169, green: 0.208, blue: 0.314)), // #E3E7EE / #2B3550
        (Color(red: 0.875, green: 0.953, blue: 0.906), Color(red: 0.114, green: 0.353, blue: 0.208)), // #DFF3E7 / #1D5A35
        (Color(red: 0.984, green: 0.922, blue: 0.839), Color(red: 0.478, green: 0.294, blue: 0.000)), // #FBEBD6 / #7A4B00
        (Color(red: 0.941, green: 0.894, blue: 0.969), Color(red: 0.357, green: 0.165, blue: 0.459)), // #F0E4F7 / #5B2A75
        (Color(red: 0.988, green: 0.890, blue: 0.894), Color(red: 0.541, green: 0.110, blue: 0.133)), // #FCE3E4 / #8A1C22
    ]

    private var letter: String {
        if let first = name?.trimmingCharacters(in: .whitespacesAndNewlines).first { return String(first).uppercased() }
        return String(speaker)
    }

    var body: some View {
        let tint = Self.tints[((speaker % Self.tints.count) + Self.tints.count) % Self.tints.count]
        Text(letter)
            .font(.footnote.weight(.semibold))
            .lineLimit(1)
            .minimumScaleFactor(0.6)
            .foregroundStyle(tint.ink)
            .frame(width: size, height: size)
            .background(tint.fill, in: Circle())
            .accessibilityHidden(true)
    }
}

// MARK: - Level-history waveform

/// The recent input level as a row of rounded bars, newest on the right: the same 0...1 peak the level meter shows,
/// sampled ten times a second. Bars are the accent and lighter blues while capture runs, muted when it does not. With
/// Reduce Motion the heights change without animation. It carries no accessibility of its own: the caller gives it a
/// label and value, or hides it.
struct LevelHistoryWaveform: View {
    /// 0...1 peak level.
    var level: Double
    /// Capture is running.
    var active = true
    var bars = 22

    @ScaledMetric(relativeTo: .largeTitle) private var height: CGFloat = 88
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var history: [Double] = []

    /// One shared tick for the screen; a publisher made in `body` would restart on every update.
    private static let tick = Timer.publish(every: 0.1, on: .main, in: .common).autoconnect()

    var body: some View {
        HStack(alignment: .center, spacing: 4) {
            ForEach(0..<bars, id: \.self) { index in
                let value = sample(index)
                RoundedRectangle(cornerRadius: 3, style: .continuous)
                    .fill(colour(value))
                    .frame(maxWidth: 8)
                    .frame(height: barHeight(value))
            }
        }
        .frame(maxWidth: .infinity)
        .frame(height: height)
        .onReceive(Self.tick) { _ in push(level) }
    }

    private func push(_ value: Double) {
        let clamped = value.isFinite ? min(max(value, 0), 1) : 0
        withAnimation(reduceMotion ? nil : .linear(duration: 0.1)) {
            history.append(clamped)
            if history.count > bars { history.removeFirst(history.count - bars) }
        }
    }

    /// History aligned to the right; empty slots on the left are silent.
    private func sample(_ index: Int) -> Double {
        let offset = bars - history.count
        return index < offset ? 0 : history[index - offset]
    }

    private func barHeight(_ value: Double) -> CGFloat {
        let floor: CGFloat = 6
        // Square root, so ordinary speech peaks (about 0.1 to 0.5) still move the bars visibly.
        return floor + (height - floor) * CGFloat(value).squareRoot()
    }

    private func colour(_ value: Double) -> Color {
        guard active else { return DictumTheme.mutedStrong.opacity(0.5) }
        if value > 0.5 { return DictumTheme.link }
        if value > 0.2 { return DictumTheme.link.opacity(0.7) }
        return DictumTheme.accent
    }
}
