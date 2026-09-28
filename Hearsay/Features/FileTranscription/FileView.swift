import AppKit
import HearsayCore
import SwiftUI
import UniformTypeIdentifiers

/// The File tab (PLAN.md 4.2): drop or choose an audio or video file,
/// transcribe it, and continue to the notes flow.
struct FileView: View {
    @Bindable var model: FileViewModel
    /// Switches the main window to the Models tab.
    var onOpenModels: () -> Void = {}

    @Environment(AIProviderStore.self) private var aiStore
    @Environment(AppSettings.self) private var settings
    @State private var notes = NotesHandoff()
    @State private var isImporterShown = false
    @State private var isDropTargeted = false

    static let acceptedTypes: [UTType] = [.audio, .movie]

    var body: some View {
        Form {
            Section {
                Picker("Language", selection: $model.languageCode) {
                    ForEach(RecordingController.languages, id: \.code) { language in
                        Text(language.label).tag(language.code)
                    }
                }
                .pickerStyle(.segmented)
                .disabled(model.isBusy)

                dropZone
            }

            status

            if let notesModel = notes.notes {
                Section("Meeting notes") {
                    NotesFlowView(model: notesModel)
                }
            }
        }
        .formStyle(.grouped)
        .fileImporter(isPresented: $isImporterShown, allowedContentTypes: Self.acceptedTypes) { result in
            if case .success(let url) = result {
                start(url)
            }
        }
        .onAppear(perform: takeNotesRequest)
        .onChange(of: model.notesRequest) { takeNotesRequest() }
    }

    private var dropZone: some View {
        VStack(spacing: 10) {
            Image(systemName: "waveform.badge.plus")
                .font(.system(size: 32))
                .foregroundStyle(.secondary)
            Text("Drop an audio or video file here")
                .font(.headline)
            Text("wav, m4a, mp3, aac, aiff, caf, or the audio track of mp4 / mov")
                .font(.caption)
                .foregroundStyle(.secondary)
            Button("Choose…") { isImporterShown = true }
                .disabled(model.isBusy)
        }
        .frame(maxWidth: .infinity, minHeight: 140)
        .padding()
        .background {
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [6]))
                .foregroundStyle(isDropTargeted ? Color.accentColor : Color.secondary.opacity(0.5))
        }
        .dropDestination(for: URL.self) { urls, _ in
            guard !model.isBusy, let url = urls.first(where: Self.isAccepted) else { return false }
            start(url)
            return true
        } isTargeted: { isDropTargeted = $0 }
    }

    @ViewBuilder private var status: some View {
        switch model.phase {
        case .idle:
            EmptyView()
        case .loading(let source):
            Section {
                HStack(spacing: 10) {
                    ProgressView().controlSize(.small)
                    Text("Reading \(source.lastPathComponent)…")
                }
            }
        case .transcribing(let source, let progress):
            Section {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Transcribing \(source.lastPathComponent)…")
                    ProgressView(value: progress)
                        .progressViewStyle(.linear)
                    Text(progress, format: .percent.precision(.fractionLength(0)))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                }
            }
        case .finished(let srt, _):
            Section("Transcript") {
                Text(srt.path)
                    .textSelection(.enabled)
                    .lineLimit(2)
                    .truncationMode(.middle)
                Button("Reveal in Finder", systemImage: "folder") { model.reveal() }
            }
        case .failed(let message):
            Section {
                Label(message, systemImage: "exclamationmark.octagon.fill")
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                if model.needsModel {
                    Button("Open Models", systemImage: "square.and.arrow.down", action: onOpenModels)
                }
            }
        }
    }

    private func start(_ url: URL) {
        notes.reset()
        model.transcribe(url)
    }

    private func takeNotesRequest() {
        guard let srt = model.takeNotesRequest() else { return }
        notes.start(srtURL: srt, languageCode: model.languageCode, store: aiStore, settings: settings)
    }

    private static func isAccepted(_ url: URL) -> Bool {
        guard let type = UTType(filenameExtension: url.pathExtension) else { return false }
        return acceptedTypes.contains { type.conforms(to: $0) }
    }
}
