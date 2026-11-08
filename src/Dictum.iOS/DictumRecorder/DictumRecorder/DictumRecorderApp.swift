import DictumBundle
import SwiftUI

@main
struct DictumRecorderApp: App {
    @State private var controller = RecordingController()

    var body: some Scene {
        WindowGroup {
            RootView(controller: controller)
        }
    }
}

/// One screen at a time, chosen by the controller's phase. No sheets or navigation stack, so the recording screen
/// cannot be swiped away.
struct RootView: View {
    @Bindable var controller: RecordingController

    var body: some View {
        switch controller.phase {
        case .consent:
            ConsentView(controller: controller)
        case .recording:
            RecordingView(controller: controller)
        case .sealing:
            if controller.startedAt != nil {
                RecordingView(controller: controller)
            } else {
                // Sealing a recovered session: nothing is being recorded, so no recording indicator.
                ProgressView(PROPOSED_Sealing).font(.title2)
            }
        case let .sealed(bundle):
            SealedView(bundle: bundle, captureFailed: controller.captureFailed) { controller.doneTapped() }
        case .recover:
            RecoverView { Task { await controller.recoverTapped() } }
        case let .failed(message):
            FailedView(message: message) { controller.doneTapped() }
        }
    }
}
