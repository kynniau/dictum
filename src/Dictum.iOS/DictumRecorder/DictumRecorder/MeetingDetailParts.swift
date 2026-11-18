import DictumBundle
import SwiftUI

// View pieces of the meeting page (MeetingDetail.swift), kept here so that file stays readable: the actions card and
// one transcript line.

/// Who does what on a kept meeting: the actions as a read-only list (a decorative marker, the action, then who and by
/// when), and under it "Edit actions", which opens the same fields as the review screen. The fields change the same
/// review state, which the page saves as before. With no actions yet the fields start open, so Add is one tap away.
struct MeetingActionsCard: View {
    @Bindable var review: MeetingReview
    let owners: [String]
    @State private var editing = false
    @ScaledMetric(relativeTo: .body) private var marker: CGFloat = 18

    /// Actions with something in them; a blank one just added shows only in the fields.
    private var listed: [MeetingReview.Action] {
        review.actions.filter { !$0.text.isEmpty || !$0.owner.isEmpty || !$0.byWhen.isEmpty }
    }

    var body: some View {
        DictumCard {
            SectionHeader(PROPOSED_ReviewActions)
            if !listed.isEmpty {
                VStack(alignment: .leading, spacing: 14) {
                    ForEach(listed) { action in row(action) }
                }
            }
            DisclosureGroup(isExpanded: $editing) {
                ReviewActionsEditor(review: review, owners: owners)
                    .padding(.top, 8)
            } label: {
                Text(PROPOSED_EditActions)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(DictumTheme.link)
            }
            .tint(DictumTheme.link)
            .frame(minHeight: DictumTheme.minTarget)
        }
        .onAppear { editing = review.actions.isEmpty }
    }

    private func row(_ action: MeetingReview.Action) -> some View {
        let who = [action.owner, action.byWhen].filter { !$0.isEmpty }.joined(separator: " · ")
        return HStack(alignment: .firstTextBaseline, spacing: 12) {
            // Not a checkbox: actions are not checkable here, so the marker is decoration only.
            Circle()
                .strokeBorder(DictumTheme.accent, lineWidth: 1.5)
                .frame(width: marker, height: marker)
                .alignmentGuide(.firstTextBaseline) { $0[.bottom] - 3 }
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                if !action.text.isEmpty {
                    Text(action.text)
                        .font(.body)
                        .foregroundStyle(DictumTheme.text)
                }
                if !who.isEmpty {
                    Text(who)
                        .font(.footnote)
                        .foregroundStyle(DictumTheme.muted)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .accessibilityElement(children: .combine)
    }
}

/// One transcript line: the speaker's badge, "name · time" muted, then the words. The caller makes it the button that
/// opens the fix-line sheet and gives it its accessibility label.
struct TranscriptLineView: View {
    let segment: MeetingRecord.Segment
    /// The speaker's label (the name given, or "Other n").
    let label: String?
    /// The name given to the speaker, for the badge's letter.
    let name: String?

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            if let speaker = segment.speaker {
                SpeakerBadge(speaker: speaker, name: name)
            }
            VStack(alignment: .leading, spacing: 2) {
                let meta = [label, segment.start.map { SpeakerSeparation.clock($0) }].compactMap { $0 }.joined(separator: " · ")
                if !meta.isEmpty {
                    Text(meta)
                        .font(.footnote.monospacedDigit())
                        .foregroundStyle(DictumTheme.muted)
                }
                Text(segment.text)
                    .font(.body)
                    .foregroundStyle(DictumTheme.text)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, 6)
    }
}
