﻿namespace XSVim
open System
open System.Collections.Generic
open MonoDevelop.Components.Commands
open MonoDevelop.Core
open MonoDevelop.Core.Text
open MonoDevelop.Ide
open MonoDevelop.Ide.Editor
open MonoDevelop.Ide.Editor.Extension

type XSVim() =
    inherit TextEditorExtension()
    let mutable disposables : IDisposable list = []
    let mutable processingKey = false
    let mutable config = { insertModeEscapeKey = None }

    let initConfig() =
        let mapping = SettingsPanel.InsertModeEscapeMapping()
        if mapping.Length = 2 then
            config <- { insertModeEscapeKey =
                           {
                               insertModeEscapeKey1 = string mapping.[0]
                               insertModeEscapeKey2 = string mapping.[1]
                               insertModeEscapeTimeout = SettingsPanel.InsertModeEscapeMappingTimeout()
                           } |> Some }
        else
            config <- { insertModeEscapeKey = None }

    member x.FileName = x.Editor.FileName.FullPath.ToString()

    member x.State
        with get() = Vim.editorStates.[x.FileName]
        and set(value) = Vim.editorStates.[x.FileName] <- value

    override x.Initialize() =
        treeViewPads.initialize()

        initConfig()
        if not (Vim.editorStates.ContainsKey x.FileName) then
            let editor = x.Editor
            let state =
                match Vim.getCaretMode editor with
                | Insert -> { VimState.Default with mode = InsertMode }
                | Block -> VimState.Default

            let state = Vim.switchToNormalMode editor state
            Vim.editorStates.Add(x.FileName, state)
            editor.GrabFocus()
            let caretChanged =
                editor.CaretPositionChanged.Subscribe
                    (fun _e ->
                        if not processingKey then // only interested in mouse clicks
                            let line = editor.GetLine editor.CaretLine
                            if line.Length > 0 && editor.CaretColumn >= line.LengthIncludingDelimiter then
                                editor.CaretOffset <- editor.CaretOffset - 1)

            let documentClosed =
                IdeApp.Workbench |> Option.ofObj
                |> Option.map(fun workbench ->
                    workbench.DocumentClosed.Subscribe
                        (fun e -> let documentName = e.Document.Name
                                  if Vim.editorStates.ContainsKey documentName then
                                      Vim.editorStates.Remove documentName |> ignore))

            let propertyChanged =
                PropertyService.PropertyChanged.Subscribe (fun _ -> initConfig())

            let focusLost =
                editor.FocusLost.Subscribe
                    (fun _ ->
                        match x.State.mode with
                        | ExMode _ ->
                            x.State <- Vim.switchToNormalMode x.Editor x.State
                            IdeApp.Workbench.StatusBar.ShowReady()
                        | _ -> ())

            disposables <- [ yield caretChanged
                             if documentClosed.IsSome then
                                yield documentClosed.Value
                             yield propertyChanged
                             yield focusLost ]

    override x.KeyPress descriptor =
        match descriptor.ModifierKeys with
        | ModifierKeys.Control
        | ModifierKeys.Command when descriptor.KeyChar = 'z' ->
            // cmd-z uses the vim undo group
            x.State.undoGroup |> Option.iter(fun d -> d.Dispose())
            EditActions.Undo x.Editor
            x.Editor.ClearSelection()
            false
        | ModifierKeys.Command when descriptor.KeyChar <> 'z' && descriptor.KeyChar <> 'r' -> false
        | _ ->
            let oldState = x.State

            processingKey <- true
            let newState, handledKeyPress = Vim.handleKeyPress x.State descriptor x.Editor config
            processingKey <- false

            match newState.statusMessage, newState.macro with
            | Some m, None -> IdeApp.Workbench.StatusBar.ShowMessage m
            | Some m, Some _ -> IdeApp.Workbench.StatusBar.ShowMessage (m + "recording")
            | None, Some _ -> IdeApp.Workbench.StatusBar.ShowMessage "recording"
            | _ -> IdeApp.Workbench.StatusBar.ShowReady()

            x.State <- newState
            match oldState.mode, newState.mode, config.insertModeEscapeKey with
            | InsertMode, InsertMode, _ when descriptor.ModifierKeys = ModifierKeys.Control && descriptor.KeyChar = 'n' ->
                false // Hack: Ctrl-N seems to be hardwired inside VS somehow to Emacs' line down
            | InsertMode, InsertMode, None ->
                base.KeyPress descriptor
            | InsertMode, InsertMode, Some escapeCombo when descriptor.KeyChar.ToString() <> escapeCombo.insertModeEscapeKey1 ->
                base.KeyPress descriptor
            | VisualMode, _, _ -> false
            | _ -> not handledKeyPress

    [<CommandUpdateHandler ("MonoDevelop.Ide.Commands.EditCommands.Undo")>]
    // We handle cmd-z ourselves to use the vim undo stack
    member x.CanUndo(ci:CommandInfo) = ci.Enabled <- false

    [<CommandUpdateHandler ("MonoDevelop.Ide.Commands.EditCommands.Rename")>]
    member x.Rename(ci:CommandInfo) =
        ci.Enabled <- true
        // dirty hack - use the command update handler to switch to insert mode
        // before the inline rename kicks in
        if x.State.mode <> InsertMode then
            x.State <- Vim.switchToInsertMode x.Editor x.State false

    override x.Dispose() =
        base.Dispose()
        disposables |> List.iter(fun d -> d.Dispose())
