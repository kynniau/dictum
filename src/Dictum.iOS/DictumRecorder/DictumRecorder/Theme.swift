import SwiftUI

// Dictum's design language for the recorder ("Chambers": editorial, light, serif titles). Every colour comes from
// site/styles.css (brand tokens, light and dark), held as colour sets in Assets.xcassets so the system switches them with
// the appearance, plus `mutedStrong` for muted words on the page background. Titles and long reading text use Apple's
// serif (New York) at weight medium with slight negative tracking; everything else is the system font; times and codes use
// monospaced digits. Corners are continuous.
//
// Accessibility rules every component keeps: 44 pt minimum targets, Dynamic Type text styles only (any fixed size goes
// through @ScaledMetric), no meaning carried by colour alone (each status has an icon and words), and the reduced-motion
// setting respected.

enum DictumTheme {
    // Brand tokens, the same in both themes.
    static let ink = Color(.dictumInk)
    static let accent = Color(.dictumAccent)
    static let accentHover = Color(.dictumAccentHover)
    static let recording = Color(.dictumRecording)
    static let working = Color(.dictumWorking)
    static let done = Color(.dictumDone)
    static let onAccent = Color(.dictumOnAccent)

    // Theme tokens, light and dark.
    static let bg = Color(.dictumBg)
    static let surface = Color(.dictumSurface)
    static let text = Color(.dictumText)
    /// Captions on `surface` only: the site notes it is under 4.5:1 on `bg`.
    static let muted = Color(.dictumMuted)
    /// Muted words on the page background (`bg`): #55607A light (5.8:1 on bg), #9AA6BA dark (7.6:1 on bg).
    static let mutedStrong = Color(.dictumMutedStrong)
    static let border = Color(.dictumBorder)
    static let borderStrong = Color(.dictumBorderStrong)
    static let accentSoft = Color(.dictumAccentSoft)
    static let link = Color(.dictumLink)
    /// The site's --mark-halo: clear in light, a hairline glow in dark so the ink mark shows on the ink page.
    static let markHalo = Color(.dictumMarkHalo)

    static let radiusControl: CGFloat = 12
    static let radiusCard: CGFloat = 20
    static let radiusButton: CGFloat = 18
    static let radiusChip: CGFloat = 10
    static let radiusPill: CGFloat = 22

    /// Readable content width on iPad, so screens do not look like a stretched phone.
    static let maxContentWidth: CGFloat = 720
    /// Apple's minimum touch target.
    static let minTarget: CGFloat = 44
}

// MARK: - Type

extension Font {
    /// Display title: the serif (New York) at weight medium.
    static func dictumDisplay(_ style: Font.TextStyle = .largeTitle) -> Font {
        .system(style, design: .serif).weight(.medium)
    }

    /// Long reading text (a summary, a transcript on a light screen): the serif at regular weight.
    static func dictumReading(_ style: Font.TextStyle = .body) -> Font { .system(style, design: .serif) }
}

extension View {
    /// A display title: serif, medium, slight negative tracking, in the text colour.
    func dictumTitle(_ style: Font.TextStyle = .largeTitle) -> some View {
        font(.dictumDisplay(style)).tracking(style == .largeTitle ? -0.6 : -0.3).foregroundStyle(DictumTheme.text)
    }

    /// Long reading text: serif body with a little extra leading, in the text colour.
    func dictumReading(_ style: Font.TextStyle = .body) -> some View {
        font(.dictumReading(style)).lineSpacing(3).foregroundStyle(DictumTheme.text)
    }

    /// The page: bg behind everything, content centred at a readable width, 24 pt gutters.
    func dictumPage() -> some View {
        frame(maxWidth: DictumTheme.maxContentWidth, alignment: .leading)
            .padding(.horizontal, 24)
            .frame(maxWidth: .infinity)
    }
}

// MARK: - Top bar

/// The mark, the word "Dictum" in the serif and the muted "Recorder · beta" tag, with an optional control on the right.
struct DictumTopBar<Trailing: View>: View {
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(spacing: 10) {
            DictumMarkView(size: 28)
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(PROPOSED_BrandName)
                    .font(.dictumDisplay(.title3))
                    .tracking(-0.3)
                    .foregroundStyle(DictumTheme.text)
                Text(PROPOSED_RecorderTag)
                    .font(.footnote)
                    .tracking(0.3)
                    .foregroundStyle(DictumTheme.mutedStrong)
            }
            Spacer(minLength: 8)
            trailing
        }
        .frame(minHeight: DictumTheme.minTarget)
        .accessibilityElement(children: .contain)
    }
}

extension DictumTopBar where Trailing == EmptyView {
    init() { trailing = EmptyView() }
}

/// The Dictum mark (site/dictum.svg as a vector asset). Decorative: the word beside it is what VoiceOver reads.
struct DictumMarkView: View {
    var size: CGFloat

    var body: some View {
        Image(.dictumMark)
            .resizable()
            .interpolation(.high)
            .scaledToFit()
            .frame(width: size, height: size)
            .shadow(color: DictumTheme.markHalo, radius: 0.8)
            .shadow(color: DictumTheme.markHalo, radius: 0.8)
            .accessibilityHidden(true)
    }
}

// MARK: - Cards and sections

/// Content on a surface card: 20 pt continuous corners, hairline border, and in light mode a very soft shadow.
struct DictumCard<Content: View>: View {
    var padding: CGFloat = 18
    var spacing: CGFloat = 12
    @ViewBuilder var content: Content

    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: DictumTheme.radiusCard, style: .continuous)
        VStack(alignment: .leading, spacing: spacing) { content }
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(DictumTheme.surface, in: shape)
            .overlay(shape.strokeBorder(DictumTheme.border, lineWidth: 1))
            .shadow(color: .black.opacity(colorScheme == .dark ? 0 : 0.04), radius: 8, y: 6)
    }
}

/// A section heading on the page or in a card, announced as a header. `.label` (the default) is a small tracked
/// uppercase label in `mutedStrong` (which passes on both bg and surface); `.heading` is a serif headline, for long or
/// approved headings that should keep the case they were written in.
struct SectionHeader: View {
    enum Style { case label, heading }

    let title: String
    var style: Style

    init(_ title: String, style: Style = .label) {
        self.title = title
        self.style = style
    }

    var body: some View {
        Group {
            switch style {
            case .label:
                Text(title)
                    .font(.caption.weight(.medium))
                    .tracking(1)
                    .textCase(.uppercase)
                    .foregroundStyle(DictumTheme.mutedStrong)
            case .heading:
                Text(title).dictumTitle(.title3)
            }
        }
        .accessibilityAddTraits(.isHeader)
    }
}

/// One fact in a facts box: label above, value below, so long values wrap at large text sizes.
struct FactRow: View {
    let label: String
    let value: String

    init(_ label: String, value: String) {
        self.label = label
        self.value = value
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.footnote).foregroundStyle(DictumTheme.muted)
            Text(value).font(.subheadline.monospacedDigit()).foregroundStyle(DictumTheme.text)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }
}

// MARK: - Buttons

/// A slight press-in, off with Reduce Motion.
private struct PressScale: ViewModifier {
    let pressed: Bool
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        content
            .scaleEffect(pressed && !reduceMotion ? 0.98 : 1)
            .animation(reduceMotion ? nil : .easeOut(duration: 0.12), value: pressed)
    }
}

/// The main action: an accent block with the on-accent label, 58 pt tall, 18 pt corners, a soft accent shadow in light
/// mode. White on #2457E6 passes AA in both themes.
struct PrimaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled
    @Environment(\.colorScheme) private var colorScheme

    func makeBody(configuration: Configuration) -> some View {
        let shape = RoundedRectangle(cornerRadius: DictumTheme.radiusButton, style: .continuous)
        configuration.label
            .font(.headline)
            .multilineTextAlignment(.center)
            .foregroundStyle(DictumTheme.onAccent)
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            .frame(maxWidth: .infinity, minHeight: 58)
            .background(configuration.isPressed ? DictumTheme.accentHover : DictumTheme.accent, in: shape)
            .shadow(color: DictumTheme.accent.opacity(colorScheme == .dark || !enabled ? 0 : 0.22), radius: 9, y: 8)
            .opacity(enabled ? 1 : 0.45)
            .contentShape(shape)
            .modifier(PressScale(pressed: configuration.isPressed))
    }
}

/// A secondary action: surface block with a strong border and the text colour.
struct SecondaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        let shape = RoundedRectangle(cornerRadius: DictumTheme.radiusButton, style: .continuous)
        configuration.label
            .font(.headline)
            .multilineTextAlignment(.center)
            .foregroundStyle(DictumTheme.text)
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            .frame(maxWidth: .infinity, minHeight: 52)
            .background(configuration.isPressed ? DictumTheme.accentSoft : DictumTheme.surface, in: shape)
            .overlay(shape.strokeBorder(DictumTheme.borderStrong, lineWidth: 1))
            .opacity(enabled ? 1 : 0.45)
            .contentShape(shape)
            .modifier(PressScale(pressed: configuration.isPressed))
    }
}

/// A small control (rename, remove, add): accent-soft block, 12 pt corners, 44 pt tall.
struct CompactButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        let shape = RoundedRectangle(cornerRadius: DictumTheme.radiusControl, style: .continuous)
        configuration.label
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(DictumTheme.text)
            .padding(.horizontal, 14)
            .frame(minWidth: DictumTheme.minTarget, minHeight: DictumTheme.minTarget)
            .background(configuration.isPressed ? DictumTheme.borderStrong : DictumTheme.accentSoft, in: shape)
            .opacity(enabled ? 1 : 0.45)
            .contentShape(shape)
    }
}

/// A text-style action (link colour, no fill), 44 pt tall, for a quieter action beside a primary one.
struct TextButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(DictumTheme.link)
            .multilineTextAlignment(.leading)
            .frame(minHeight: DictumTheme.minTarget)
            .opacity(configuration.isPressed ? 0.6 : (enabled ? 1 : 0.45))
            .contentShape(Rectangle())
    }
}

extension ButtonStyle where Self == PrimaryButtonStyle {
    static var dictumPrimary: PrimaryButtonStyle { PrimaryButtonStyle() }
}

extension ButtonStyle where Self == SecondaryButtonStyle {
    static var dictumSecondary: SecondaryButtonStyle { SecondaryButtonStyle() }
}

extension ButtonStyle where Self == CompactButtonStyle {
    static var dictumCompact: CompactButtonStyle { CompactButtonStyle() }
}

extension ButtonStyle where Self == TextButtonStyle {
    static var dictumText: TextButtonStyle { TextButtonStyle() }
}

/// `Button(title, action:)` in the primary style, with its accessibility label.
struct PrimaryButton: View {
    let title: String
    var systemImage: String?
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            if let systemImage { Label(title, systemImage: systemImage) } else { Text(title) }
        }
        .buttonStyle(.dictumPrimary)
        .accessibilityLabel(title)
    }
}

/// `Button(title, action:)` in the secondary style, with its accessibility label.
struct SecondaryButton: View {
    let title: String
    var systemImage: String?
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            if let systemImage { Label(title, systemImage: systemImage) } else { Text(title) }
        }
        .buttonStyle(.dictumSecondary)
        .accessibilityLabel(title)
    }
}

// MARK: - Status

/// Recording (red), working (amber) or done (green), as a quiet chip. Always an icon plus words; the colour is only a
/// tint on the icon and a soft background, and the words stay in the text colour so contrast never depends on the
/// status colour.
struct StatusPill: View {
    enum Kind {
        case recording, working, done, warning

        var colour: Color {
            switch self {
            case .recording: return DictumTheme.recording
            case .working, .warning: return DictumTheme.working
            case .done: return DictumTheme.done
            }
        }

        var icon: String {
            switch self {
            case .recording: return "record.circle"
            case .working: return "hourglass"
            case .done: return "checkmark.circle.fill"
            case .warning: return "exclamationmark.triangle.fill"
            }
        }
    }

    let kind: Kind
    let text: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Image(systemName: kind.icon)
                .foregroundStyle(kind.colour)
                .accessibilityHidden(true)
            Text(text)
                .foregroundStyle(DictumTheme.text)
                .multilineTextAlignment(.leading)
        }
        .font(.footnote.weight(.semibold))
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(kind.colour.opacity(0.14), in: RoundedRectangle(cornerRadius: DictumTheme.radiusChip, style: .continuous))
        .accessibilityElement(children: .combine)
    }
}

// MARK: - Level meter

/// The mark's three bars as an input-level meter: short, tall, medium (site/dictum.svg bars of 16, 34 and 22 in a bubble
/// 56 wide and 52 tall, corner 14) inside a tile shaped like the mark's bubble, ink in light and surface in dark. Bars
/// are on-accent white at rest and recording red while capture runs. With `level` nil it is static (the mark's own
/// proportions, no animation). Heights rise fast and fall over about 300 ms; the outer bars follow the middle a moment
/// later and a little lower, so they move like a wave. With Reduce Motion the heights change without animation.
/// It carries no accessibility of its own: the caller gives it a label and value, or hides it.
struct DictumLevelMeter: View {
    /// 0...1 peak level, or nil for the static illustration.
    var level: Double?
    /// Capture is running: bars tinted recording red.
    var active = false
    var height: CGFloat = 64

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.colorScheme) private var colorScheme
    @State private var middle: Double = 0
    @State private var outer: Double = 0

    /// The mark's bar heights over the tallest bar, left to right.
    private static let proportions: [CGFloat] = [16.0 / 34, 1, 22.0 / 34]

    var body: some View {
        // The mark's bubble is 56 × 52 with its tail below, in a 64 × 64 box; everything scales from `height`.
        let unit = height / 64
        let width = 56 * unit
        let bubble = 52 * unit
        ZStack(alignment: .topLeading) {
            BubbleTail()
                .fill(tile)
                .frame(width: 13 * unit, height: 13 * unit)
                .offset(x: 10 * unit, y: bubble - 0.5)
            RoundedRectangle(cornerRadius: 14 * unit, style: .continuous)
                .fill(tile)
                .frame(width: width, height: bubble)
            HStack(alignment: .center, spacing: 5 * unit) {
                ForEach(0..<3, id: \.self) { index in
                    Capsule()
                        .fill(active ? DictumTheme.recording : DictumTheme.onAccent)
                        .frame(width: 6 * unit, height: barHeight(index, unit: unit))
                }
            }
            .frame(width: width, height: bubble)
        }
        .frame(width: width, height: height, alignment: .topLeading)
        .onChange(of: level) { old, new in
            guard let new else { return }
            let rising = new > (old ?? 0)
            let move: Animation = rising ? .easeOut(duration: 0.08) : .easeOut(duration: 0.3)
            withAnimation(reduceMotion ? nil : move) { middle = new }
            withAnimation(reduceMotion ? nil : move.delay(0.07)) { outer = new * 0.8 }
        }
    }

    /// Ink in light, as the mark; the surface colour in dark, so the tile shows on the ink page.
    private var tile: Color { colorScheme == .dark ? DictumTheme.surface : DictumTheme.ink }

    private func barHeight(_ index: Int, unit: CGFloat) -> CGFloat {
        let tallest = 34 * unit
        let full = tallest * Self.proportions[index]
        guard level != nil else { return full }
        // Square root, so ordinary speech peaks (about 0.1 to 0.5) still move the bars visibly.
        let value = index == 1 ? middle : outer
        let fraction = 0.25 + 0.75 * CGFloat(min(max(value, 0), 1)).squareRoot()
        return max(6 * unit, full * fraction)
    }
}

/// The mark's speech-bubble tail (path M14 50 L14 63 L27 50 Z), drawn in a square.
private struct BubbleTail: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.minX, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY))
        path.closeSubpath()
        return path
    }
}
