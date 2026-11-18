import DictumBundle
import SwiftUI

@main
struct DictumRecorderApp: App {
    @State private var controller = RecordingController()

    var body: some Scene {
        WindowGroup {
            RootView(controller: controller)
                .preferredColorScheme(controller.settings.colorScheme)
        }
    }
}

/// One screen at a time, chosen by the controller's phase. No navigation stack, and the only sheets are on the home and
/// end-of-meeting screens, so the recording screen cannot be swiped away.
struct RootView: View {
    @Bindable var controller: RecordingController
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        screen
            .onChange(of: scenePhase) { _, phase in
                if phase == .background { controller.enteredBackground() }
            }
            // A sound file handed over by the share menu ("Open in Dictum Recorder").
            .onOpenURL { url in
                Task { await controller.openedFile(url) }
            }
    }

    @ViewBuilder private var screen: some View {
        switch controller.phase {
        case .home:
            HomeView(controller: controller)
        case .consent:
            ConsentView(controller: controller)
        case .importing:
            ImportView(controller: controller)
        case .recording:
            RecordingView(controller: controller)
        case .sealing:
            if controller.startedAt != nil {
                RecordingView(controller: controller)
            } else {
                // Sealing a recovered session: nothing is being recorded, so no recording indicator.
                SealingRecoveredView()
            }
        case let .review(bundle):
            ReviewView(controller: controller, bundle: bundle)
        case let .sealed(bundle):
            SealedView(bundle: bundle, captureFailed: controller.captureFailed, keepFailed: controller.keepFailed,
                       email: SummaryEmail.make(from: controller.review,
                                                footer: controller.keepThisMeeting ? PROPOSED_EmailKeptLine : PROPOSED_EmailSealedLine)) {
                controller.doneTapped()
            }
        case .recover:
            RecoverView { Task { await controller.recoverTapped() } }
        case let .failed(message):
            FailedView(message: message) { controller.doneTapped() }
        case .detail:
            if let detail = controller.detail {
                MeetingDetailView(model: detail, settings: controller.settings,
                                  close: { controller.closeDetail() }, deleted: { controller.detailDeleted() })
            } else {
                HomeView(controller: controller)
            }
        }
    }
}
