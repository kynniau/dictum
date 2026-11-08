import SwiftUI

// Dictum's design language for the recorder. Every colour comes from site/styles.css (brand tokens, light and dark),
// held as colour sets in Assets.xcassets so the system switches them with the appearance. Radii are the site's
// --radius-control, --radius-card and --radius-pill. Type is the system font: titles at weight 600 with slight negative
// tracking (the site's display face), monospaced digits for times and codes.
//
// Accessibility rules every component keeps: 44 pt minimum targets, Dynamic Type text styles only, no meaning carried
// by colour alone (each status has an icon and words), and the reduced-motion setting respected.

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
    static let border = Color(.dictumBorder)
    static let borderStrong = Color(.dictumBorderStrong)
    static let accentSoft = Color(.dictumAccentSoft)
    static let link = Color(.dictumLink)
    /// The site's --mark-halo: clear in light, a hairline glow in dark so the ink mark shows on the ink page.
    static let markHalo = Color(.dictumMarkHalo)

    static let radiusControl: CGFloat = 8
    static let radiusCard: CGFloat = 12
    static let radiusPill: CGFloat = 22

    /// Readable content width on iPad, so screens do not look like a stretched phone.
    static let maxContentWidth: CGFloat = 720
    /// Apple's minimum touch target.
    static let minTarget: CGFloat = 44
}

// MARK: - Type

extension Font {
    /// Display title: weight 600 (semibold).
    static func dictumDisplay(_ style: Font.TextStyle = .largeTitle) -> Font { .system(style).weight(.semibold) }
}

extension View {
    /// A display title: semibold with slight negative tracking, in the text colour.
    func dictumTitle(_ style: Font.TextStyle = .largeTitle) -> some View {
        font(.dictumDisplay(style)).tracking(-0.4).foregroundStyle(DictumTheme.text)
    }

    /// The page: bg behind everything, content centred at a readable width, 20 pt gutters.
    func dictumPage() -> some View {
        frame(maxWidth: DictumTheme.maxContentWidth, alignment: .leading)
            .padding(.horizontal, 20)
            .frame(maxWidth: .infinity)
    }
}

// MARK: - Top bar

/// The mark, the word "Dictum" and the muted "Recorder · beta" tag, with an optional control on the right.
struct DictumTopBar<Trailing: View>: View {
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(spacing: 10) {
            DictumMarkView(size: 28)
            Text(PROPOSED_BrandName)
                .font(.dictumDisplay(.title3))
                .tracking(-0.3)
                .foregroundStyle(DictumTheme.text)
            Text(PROPOSED_RecorderTag)
                .font(.footnote.weight(.medium))
                .foregroundStyle(DictumTheme.text.opacity(0.75))
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .background(DictumTheme.accentSoft, in: Capsule())
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

/// Content on a surface card: 12 pt radius, hairline border.
struct DictumCard<Content: View>: View {
    var padding: CGFloat = 16
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 12) { content }
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(DictumTheme.surface, in: RoundedRectangle(cornerRadius: DictumTheme.radiusCard))
            .overlay(RoundedRectangle(cornerRadius: DictumTheme.radiusCard).strokeBorder(DictumTheme.border, lineWidth: 1))
    }
}

/// A section heading on the page or in a card, announced as a header.
struct SectionHeader: View {
    let title: String

    init(_ title: String) { self.title = title }

    var body: some View {
        Text(title)
            .dictumTitle(.headline)
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

/// The main action: an accent pill with the on-accent label. White on #2457E6 passes AA in both themes.
struct PrimaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.headline)
            .multilineTextAlignment(.center)
            .foregroundStyle(DictumTheme.onAccent)
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            .frame(maxWidth: .infinity, minHeight: 52)
            .background(configuration.isPressed ? DictumTheme.accentHover : DictumTheme.accent,
                        in: RoundedRectangle(cornerRadius: DictumTheme.radiusPill))
            .opacity(enabled ? 1 : 0.45)
            .contentShape(RoundedRectangle(cornerRadius: DictumTheme.radiusPill))
    }
}

/// A secondary action: surface pill with a strong border and the text colour.
struct SecondaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.headline)
            .multilineTextAlignment(.center)
            .foregroundStyle(DictumTheme.text)
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            .frame(maxWidth: .infinity, minHeight: 52)
            .background(configuration.isPressed ? DictumTheme.accentSoft : DictumTheme.surface,
                        in: RoundedRectangle(cornerRadius: DictumTheme.radiusPill))
            .overlay(RoundedRectangle(cornerRadius: DictumTheme.radiusPill).strokeBorder(DictumTheme.borderStrong, lineWidth: 1))
            .opacity(enabled ? 1 : 0.45)
            .contentShape(RoundedRectangle(cornerRadius: DictumTheme.radiusPill))
    }
}

/// A small control (rename, remove, add): accent-soft capsule, 44 pt tall.
struct CompactButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(DictumTheme.text)
            .padding(.horizontal, 14)
            .frame(minWidth: DictumTheme.minTarget, minHeight: DictumTheme.minTarget)
            .background(configuration.isPressed ? DictumTheme.borderStrong : DictumTheme.accentSoft,
                        in: RoundedRectangle(cornerRadius: DictumTheme.radiusControl))
            .opacity(enabled ? 1 : 0.45)
            .contentShape(RoundedRectangle(cornerRadius: DictumTheme.radiusControl))
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

/// Recording (red), working (amber) or done (green). Always an icon plus words; the colour is only a tint on the icon
/// and a soft background, and the words stay in the text colour so contrast never depends on the status colour.
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
        HStack(spacing: 8) {
            Image(systemName: kind.icon)
                .foregroundStyle(kind.colour)
                .accessibilityHidden(true)
            Text(text)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(DictumTheme.text)
                .multilineTextAlignment(.leading)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(kind.colour.opacity(0.14), in: RoundedRectangle(cornerRadius: DictumTheme.radiusPill))
        .overlay(RoundedRectangle(cornerRadius: DictumTheme.radiusPill).strokeBorder(kind.colour.opacity(0.45), lineWidth: 1))
        .accessibilityElement(children: .combine)
    }
}
