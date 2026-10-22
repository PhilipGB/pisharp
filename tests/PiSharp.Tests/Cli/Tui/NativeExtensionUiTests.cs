using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class NativeExtensionUiTests
{
    [Fact]
    public void ParsedInputListenersRewriteInRegistrationOrderAndRespectReservedKeys()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        var factory = (IPiSharpExtensionUiPackageFactory)ui;
        using var first = factory.CreatePackageScope("first.dll");
        using var second = factory.CreatePackageScope("second.dll");
        var secondInput = "";
        var secondCalls = 0;
        using var firstSubscription = first.OnTerminalInput(_ =>
            PiSharpExtensionTerminalInputResult.Rewrite("rewritten"));
        using var secondSubscription = second.OnTerminalInput(input =>
        {
            secondCalls++;
            secondInput = input.Text ?? "";
            return PiSharpExtensionTerminalInputResult.Consume;
        });
        var original = new TerminalInputEvent(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false), null);

        var intercepted = ui.InterceptTerminalInput(original, reserved: false);

        Assert.True(intercepted.Consumed);
        Assert.Equal("rewritten", secondInput);
        Assert.Equal(1, secondCalls);

        var reserved = ui.InterceptTerminalInput(original, reserved: true);
        Assert.False(reserved.Consumed);
        Assert.Same(original, reserved.Input);
        Assert.Equal(1, secondCalls);

        secondSubscription.Dispose();
        var passthrough = ui.InterceptTerminalInput(original, reserved: false);
        Assert.False(passthrough.Consumed);
        Assert.Equal("rewritten", passthrough.Input.Text);
        Assert.Equal(1, secondCalls);

        firstSubscription.Dispose();
        PiSharpExtensionUiInputEvent? decoded = null;
        using var decodedSubscription = first.OnTerminalInput(input =>
        {
            decoded = input;
            return PiSharpExtensionTerminalInputResult.PassThrough;
        });
        _ = ui.InterceptTerminalInput(original, reserved: false);
        Assert.Equal("x", decoded?.Text);
        Assert.False(decoded?.IsPaste);
        Assert.Equal("x", decoded?.RawData);
    }

    [Fact]
    public void RawInputCannotConsumeTheConfiguredExitKeyOnAnEmptyEditor()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("exit-key.dll");
        var calls = 0;
        using var subscription = package.OnTerminalInput(_ =>
        {
            calls++;
            return PiSharpExtensionTerminalInputResult.Consume;
        });

        var exit = new TerminalInputEvent(new ConsoleKeyInfo('\x04', ConsoleKey.D, false, false, true), null);
        Assert.True(editor.IsReservedExtensionInput(exit));
        var interception = ui.InterceptTerminalInput(exit, editor.IsReservedExtensionInput(exit));
        Assert.False(interception.Consumed);
        Assert.Equal(0, calls);

        editor.Prefill("draft");
        Assert.False(editor.IsReservedExtensionInput(exit));
        interception = ui.InterceptTerminalInput(exit, editor.IsReservedExtensionInput(exit));
        Assert.True(interception.Consumed);
        Assert.Equal(1, calls);
        Assert.Equal("draft", editor.Draft);
    }

    [Fact]
    public async Task RemovingPackageWhileInputCallbackUnsubscribesDoesNotDeadlock()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("racing-input.dll");
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        IDisposable subscription = null!;
        subscription = package.OnTerminalInput(_ =>
        {
            callbackEntered.Set();
            releaseCallback.Wait();
            subscription.Dispose();
            return PiSharpExtensionTerminalInputResult.PassThrough;
        });
        var input = new TerminalInputEvent(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false), null);
        var interception = Task.Run(() => ui.InterceptTerminalInput(input, reserved: false));
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(2)));

        Exception? removalFailure = null;
        using var removalStarted = new ManualResetEventSlim();
        var removal = new Thread(() =>
        {
            removalStarted.Set();
            try { package.Dispose(); }
            catch (Exception exception) { removalFailure = exception; }
        });
        removal.Start();
        Assert.True(removalStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => (removal.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(2)));

        releaseCallback.Set();
        Assert.False((await interception.WaitAsync(TimeSpan.FromSeconds(2))).Consumed);
        Assert.True(removal.Join(TimeSpan.FromSeconds(2)));
        Assert.Null(removalFailure);
        subscription.Dispose();
    }

    [Fact]
    public void ExtensionInputEventsDistinguishTypedTextFromBracketedPaste()
    {
        var typed = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("x"))).Read();
        var pasted = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[200~x\u001b[201~"))).Read();

        Assert.False(NativeExtensionInteractiveUiHost.ToExtensionInput(typed).IsPaste);
        Assert.True(NativeExtensionInteractiveUiHost.ToExtensionInput(pasted).IsPaste);
        Assert.Equal("x", NativeExtensionInteractiveUiHost.ToExtensionInput(pasted).Text);
        Assert.Equal("\u001b[200~x\u001b[201~", NativeExtensionInteractiveUiHost.ToExtensionInput(pasted).RawData);
    }

    [Fact]
    public void RawInputListenerReceivesOriginalEscapeAndMouseSequences()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("raw-input.dll");
        var received = new List<PiSharpExtensionUiInputEvent>();
        using var subscription = package.OnTerminalInput(input =>
        {
            received.Add(input);
            return PiSharpExtensionTerminalInputResult.Consume;
        });

        var escape = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[99~"))).Read();
        var mouse = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[<0;4;2M"))).Read();

        Assert.False(ui.InterceptTerminalInput(escape, reserved: true).Consumed);
        Assert.True(ui.InterceptTerminalInput(escape, reserved: false).Consumed);
        Assert.True(ui.InterceptTerminalInput(mouse, reserved: false).Consumed);
        Assert.Equal("\u001b[99~", received[0].RawData);
        Assert.Equal("\u001b[<0;4;2M", received[1].RawData);
        Assert.Equal(new PiSharpExtensionUiMouseEvent(0, 4, 2, false), received[1].Mouse);
    }

    [Fact]
    public async Task RawInputListenerInterceptsTerminalStreamBeforeTheEditor()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("editor-input.dll");
        var received = 0;
        using var subscription = package.OnTerminalInput(input =>
        {
            if (input.RawData != "!") return PiSharpExtensionTerminalInputResult.PassThrough;
            received++;
            return PiSharpExtensionTerminalInputResult.Consume;
        });

        var result = await editor.HandleActiveInputAsync(
            new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("!"))).Read(),
            (_, _, _) => Task.FromResult(false), () => [], () => { });

        Assert.True(result);
        Assert.Equal(1, received);
        Assert.Empty(editor.Draft);
    }

    [Fact]
    public async Task DisposingAnOlderScreenBindingDoesNotClearTheCurrentEditorHooks()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var firstOutput = new SynchronizedStringWriter();
        using var secondOutput = new SynchronizedStringWriter();
        using var firstScreen = new TerminalScreen(firstOutput, error, () => 48, () => 16);
        using var secondScreen = new TerminalScreen(secondOutput, error, () => 48, () => 16);
        var editor = new TerminalEditor();
        editor.AttachScreen(firstScreen);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        var oldNotifications = 0;
        ui.SetInteractiveNotificationHandler((_, _) => oldNotifications++);
        using var firstBinding = ui.BindInteractiveScreen(firstScreen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("current-screen.dll");
        package.SetWidget("current", ["current-screen-widget"]);
        Assert.True(SpinWait.SpinUntil(() => firstOutput.ToString().Contains(
            "current-screen-widget", StringComparison.Ordinal), TimeSpan.FromSeconds(2)));

        editor.AttachScreen(secondScreen);
        using var currentBinding = ui.BindInteractiveScreen(secondScreen, editor);
        var currentNotifications = 0;
        ui.SetInteractiveNotificationHandler((_, _) => currentNotifications++);
        Assert.True(SpinWait.SpinUntil(() => secondOutput.ToString().Contains(
            "current-screen-widget", StringComparison.Ordinal), TimeSpan.FromSeconds(2)));
        var intercepted = 0;
        using var subscription = package.OnTerminalInput(input =>
        {
            if (input.RawData != "!") return PiSharpExtensionTerminalInputResult.PassThrough;
            intercepted++;
            return PiSharpExtensionTerminalInputResult.Consume;
        });

        firstBinding.Dispose();
        package.Notify("current screen notification");
        var handled = await editor.HandleActiveInputAsync(
            new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("!"))).Read(),
            (_, _, _) => Task.FromResult(false), () => [], () => { });

        Assert.True(handled);
        Assert.Equal(1, intercepted);
        Assert.Equal(0, oldNotifications);
        Assert.Equal(1, currentNotifications);
        Assert.Empty(editor.Draft);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frames = TerminalOutputFrameReader.Read(firstOutput.ToString(), rows: 16, columns: 48);
            return frames.Count > 0 && !frames[^1].Screen.Contains("current-screen-widget", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
        Assert.Contains("current-screen-widget", TerminalOutputFrameReader.Read(secondOutput.ToString(),
            rows: 16, columns: 48)[^1].Screen, StringComparison.Ordinal);
        currentBinding.Dispose();
        package.Notify("after screen shutdown");
        Assert.Equal(1, currentNotifications);
    }

    [Fact]
    public async Task DisposingAnOlderBindingForTheSameScreenPreservesTheCurrentEditorHooks()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var firstBinding = ui.BindInteractiveScreen(screen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("same-screen-binding.dll");
        package.SetWidget("current", ["same-screen-widget"]);
        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains(
            "same-screen-widget", StringComparison.Ordinal), TimeSpan.FromSeconds(2)));

        using var currentBinding = ui.BindInteractiveScreen(screen, editor);
        var notifications = 0;
        ui.SetInteractiveNotificationHandler((_, _) => notifications++);
        var intercepted = 0;
        using var subscription = package.OnTerminalInput(input =>
        {
            if (input.RawData != "!") return PiSharpExtensionTerminalInputResult.PassThrough;
            intercepted++;
            return PiSharpExtensionTerminalInputResult.Consume;
        });

        firstBinding.Dispose();
        package.Notify("current same-screen notification");
        var handled = await editor.HandleActiveInputAsync(
            new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("!"))).Read(),
            (_, _, _) => Task.FromResult(false), () => [], () => { });

        Assert.True(handled);
        Assert.Equal(1, intercepted);
        Assert.Equal(1, notifications);
        Assert.Empty(editor.Draft);
        Assert.Contains("same-screen-widget", TerminalOutputFrameReader.Read(output.ToString(),
            rows: 16, columns: 48)[^1].Screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InFlightPresentationRenderCannotRestoreWidgetsAfterScreenShutdown()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("blocked-render.dll");
        using var renderEntered = new ManualResetEventSlim();
        using var releaseRender = new ManualResetEventSlim();
        var component = new BlockingRenderWidget(renderEntered, releaseRender);

        package.SetWidget("blocked", component);
        Assert.True(renderEntered.Wait(TimeSpan.FromSeconds(2)));
        binding.Dispose();
        releaseRender.Set();
        await component.RenderReturned.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48);
        Assert.NotEmpty(frames);
        Assert.DoesNotContain("stale widget", frames[^1].Screen, StringComparison.Ordinal);
    }

    [Fact]
    public void RawInputReplacementParsesKeySequencesForRemainingListeners()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("rewrite-sequence.dll");
        var rewriterCalls = 0;
        var followingInputs = new List<PiSharpExtensionUiInputEvent>();
        using var rewriter = package.OnTerminalInput(input =>
        {
            rewriterCalls++;
            return input.RawData == "x"
                ? PiSharpExtensionTerminalInputResult.Rewrite("\u001b[97;1u\u001b[98;1u")
                : PiSharpExtensionTerminalInputResult.PassThrough;
        });
        using var observer = package.OnTerminalInput(input =>
        {
            followingInputs.Add(input);
            return PiSharpExtensionTerminalInputResult.PassThrough;
        });
        var original = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("x"))).Read();

        var interception = ui.InterceptTerminalInput(original, reserved: false);

        Assert.False(interception.Consumed);
        Assert.Equal(ConsoleKey.A, interception.Input.Key?.Key);
        Assert.Equal(ConsoleKey.B, Assert.Single(interception.Following!).Key?.Key);
        Assert.Equal(new[] { "a", "b" }, followingInputs.Select(input => input.Text));
        Assert.Equal(1, rewriterCalls);
    }

    [Fact]
    public void RewrittenReservedKeyBypassesLaterExtensionListeners()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        var factory = (IPiSharpExtensionUiPackageFactory)ui;
        using var first = factory.CreatePackageScope("rewrite-reserved.dll");
        using var second = factory.CreatePackageScope("consume-reserved.dll");
        var laterListenerCalls = 0;
        using var firstSubscription = first.OnTerminalInput(_ =>
            PiSharpExtensionTerminalInputResult.Rewrite("\u0003"));
        using var secondSubscription = second.OnTerminalInput(_ =>
        {
            laterListenerCalls++;
            return PiSharpExtensionTerminalInputResult.Consume;
        });
        var original = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("x"))).Read();

        var interception = ui.InterceptTerminalInput(original, reserved: false);

        Assert.False(interception.Consumed);
        Assert.Equal(ConsoleKey.C, interception.Input.Key?.Key);
        Assert.True(interception.Input.Key?.Modifiers.HasFlag(ConsoleModifiers.Control));
        Assert.Equal(0, laterListenerCalls);
    }

    [Fact]
    public async Task FocusedCustomComponentReceivesParsedMouseInput()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("custom-mouse.dll");
        var component = new MouseTrackingComponent();
        var operation = package.ShowCustomAsync<string>(_ => component);
        var mouse = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[<0;4;2M"))).Read();

        var interception = ui.InterceptTerminalInput(mouse, reserved: false);

        Assert.True(interception.Consumed);
        Assert.Equal(new PiSharpExtensionUiMouseEvent(0, 4, 2, false), component.Mouse);
        package.Dispose();
        try { _ = await operation.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PackageInputListenersRunBeforeFocusedCustomComponents()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("listener-first.dll");
        var component = new TestComponent(_ => { });
        var listenerCalls = 0;
        using var subscription = package.OnTerminalInput(_ =>
        {
            listenerCalls++;
            return PiSharpExtensionTerminalInputResult.Consume;
        });
        var operation = package.ShowCustomAsync<string>(_ => component);
        var input = new TerminalInputEvent(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false), null);

        var interception = ui.InterceptTerminalInput(input, reserved: false);

        Assert.True(interception.Consumed);
        Assert.Equal(1, listenerCalls);
        Assert.Equal(0, component.HandleCount);
        package.Dispose();
        try { _ = await operation.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { }
        Assert.Equal(1, component.DisposeCount);
    }

    [Fact]
    public async Task CustomComponentCompletesOnceAndDisposesAfterFocusReturns()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        editor.Prefill("draft before custom UI");
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        var factory = (IPiSharpExtensionUiPackageFactory)ui;
        using var package = factory.CreatePackageScope("custom.dll");
        TestComponent? component = null;

        var operation = package.ShowCustomAsync<string>(complete =>
            component = new TestComponent(complete));
        Assert.NotNull(component);
        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("custom 48x16", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2)));
        var input = new TerminalInputEvent(new ConsoleKeyInfo('\n', ConsoleKey.Enter, false, false, false), null);

        var interception = ui.InterceptTerminalInput(input, reserved: false);
        var completion = await operation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(interception.Consumed);
        Assert.False(completion.IsCancelled);
        Assert.Equal("submitted", completion.Value);
        Assert.Equal(1, component.HandleCount);
        Assert.Equal(1, component.DisposeCount);
        Assert.Equal("draft before custom UI", editor.Draft);
        Assert.Equal(0, ui.InterceptTerminalInput(input, reserved: false).Consumed ? 1 : 0);
    }

    [Fact]
    public async Task CompletingFromInputDefersComponentDisposalUntilCallbackReturns()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("complete-in-input.dll");
        var component = new CompletingComponent();
        var operation = package.ShowCustomAsync<string>(complete =>
        {
            component.SetCompletion(complete);
            return component;
        });
        var input = new TerminalInputEvent(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false), null);

        Assert.True(ui.InterceptTerminalInput(input, reserved: false).Consumed);
        var completion = await operation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(component.WasDisposedInsideInput);
        Assert.True(component.IsDisposed);
        Assert.Equal("completed", completion.Value);
    }

    [Fact]
    public async Task CompletingFromResizeSkipsRenderAndDisposesAfterResizeReturns()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("complete-on-resize.dll");
        var component = new CompletingOnResizeComponent();

        var operation = package.ShowCustomAsync<string>(complete =>
        {
            component.SetCompletion(complete);
            return component;
        });
        var completion = await operation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(completion.IsCancelled);
        Assert.Equal("resized", completion.Value);
        Assert.Equal(0, component.RenderCount);
        Assert.True(SpinWait.SpinUntil(() => component.DisposeCount == 1, TimeSpan.FromSeconds(2)));
        Assert.False(component.WasDisposedInsideResize);
        Assert.Equal(1, component.DisposeCount);
    }

    [Fact]
    public async Task CustomComponentRenderFailureCancelsAndDisposesAfterRenderReturns()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("render-failure.dll");
        var component = new ThrowingRenderComponent();

        var operation = package.ShowCustomAsync<string>(_ => component);
        var completion = await operation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(completion.IsCancelled);
        Assert.True(component.RenderCount > 0);
        Assert.True(SpinWait.SpinUntil(() => component.DisposeCount == 1, TimeSpan.FromSeconds(2)));
        Assert.False(component.WasDisposedInsideRender);
        Assert.Equal(1, component.DisposeCount);
        Assert.Contains("expected custom render failure", error.ToString(), StringComparison.OrdinalIgnoreCase);
        var input = new TerminalInputEvent(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false), null);
        Assert.False(ui.InterceptTerminalInput(input, reserved: false).Consumed);
    }

    [Fact]
    public void CustomComponentCompletionRacingFactoryAttachmentStillDisposesExactlyOnce()
    {
        Parallel.For(0, 256, _ =>
        {
            var component = new RecordingWidget("racing component");
            var request = new NativeExtensionCustomComponentRequest<string>(Guid.NewGuid(), new(), _ => { }, _ => { });

            Parallel.Invoke(
                () => request.Attach(component),
                () => request.Complete("completed"));

            request.DisposeComponent();

            Assert.Equal(1, component.DisposeCount);
        });
    }

    [Fact]
    public async Task OverlayCustomComponentUsesRequestedPlacementAndCancelsOnEscape()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        var columns = 64;
        var rows = 20;
        using var screen = new TerminalScreen(output, error, () => columns, () => rows);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("overlay.dll");
        var component = new TestComponent(_ => { });
        var operation = package.ShowCustomAsync<string>(_ => component,
            new(PiSharpExtensionUiHostingMode.Overlay, new(Left: 3, Top: 2, Width: 28, Height: 8)));

        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("custom 64x20", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2)));
        var resizeCount = component.ResizeCount;
        columns = 48;
        rows = 16;
        screen.RefreshIfResized();
        Assert.True(SpinWait.SpinUntil(() => component.ResizeCount > resizeCount, TimeSpan.FromSeconds(2)));

        var escape = new TerminalInputEvent(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false), null);
        Assert.True(ui.InterceptTerminalInput(escape, reserved: false).Consumed);
        Assert.True((await operation.WaitAsync(TimeSpan.FromSeconds(2))).IsCancelled);
        Assert.Equal(1, component.DisposeCount);
    }

    [Fact]
    public async Task ExtensionSelectionDialogAcceptsMouseOptionClicks()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 64, () => 20);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("selection-mouse.dll");
        var operation = package.SelectAsync("Choose a region", ["North", "South"]);

        Assert.True(SpinWait.SpinUntil(() =>
        {
            var visibleFrames = TerminalOutputFrameReader.Read(output.ToString(), rows: 20, columns: 64);
            return visibleFrames.Count > 0 && visibleFrames[^1].Screen.Split('\n')
                .Any(row => row.Contains("South", StringComparison.Ordinal));
        }, TimeSpan.FromSeconds(2)));
        var frame = TerminalOutputFrameReader.Read(output.ToString(), rows: 20, columns: 64)[^1].Screen;
        var rows = frame.Split('\n').Select(TerminalTextLayout.StripFormatting).ToArray();
        var targetRow = Array.FindIndex(rows, row => row.Contains("South", StringComparison.Ordinal));
        Assert.True(targetRow >= 0);
        var targetColumn = rows[targetRow].IndexOf("South", StringComparison.Ordinal) + 1;
        Assert.True(targetColumn > 0);

        var press = new TerminalInputEvent(null, null, new TerminalMouseEvent(0, targetColumn, targetRow + 1, false));
        var release = press with { Mouse = new TerminalMouseEvent(0, targetColumn, targetRow + 1, true) };
        Assert.True(ui.InterceptTerminalInput(press, reserved: false).Consumed);
        Assert.True(ui.InterceptTerminalInput(release, reserved: false).Consumed);
        Assert.Equal("South", await operation.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task EscapeCancelsCustomComponentAndPackageRemovalRevokesItsHandle()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        var factory = (IPiSharpExtensionUiPackageFactory)ui;
        var package = factory.CreatePackageScope("cancel.dll");
        var component = new TestComponent(_ => { });
        var operation = package.ShowCustomAsync<string>(_ => component);

        var escape = new TerminalInputEvent(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false), null);
        Assert.True(ui.InterceptTerminalInput(escape, reserved: false).Consumed);
        var completion = await operation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completion.IsCancelled);
        Assert.Equal(1, component.DisposeCount);

        var removedComponent = new TestComponent(_ => { });
        var removedOperation = package.ShowCustomAsync<string>(_ => removedComponent);
        package.Dispose();
        try
        {
            await removedOperation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
        Assert.Equal(1, removedComponent.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => package.SetStatus("late", "ignored"));
    }

    [Fact]
    public async Task ApplicationInterruptCancelsCustomComponentAndPassesThroughToTheHost()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("interrupt.dll");
        var component = new TestComponent(_ => { });
        var operation = package.ShowCustomAsync<string>(_ => component);
        var interrupt = new TerminalInputEvent(new ConsoleKeyInfo('\x03', ConsoleKey.C, false, false, true), null);

        var interception = ui.InterceptTerminalInput(interrupt, reserved: true);

        Assert.False(interception.Consumed);
        Assert.True((await operation.WaitAsync(TimeSpan.FromSeconds(2))).IsCancelled);
        Assert.Equal(1, component.DisposeCount);
    }

    [Fact]
    public async Task RepeatedCustomUiCompletionCancellationAndShutdownReleaseEveryComponent()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("custom-ui-stress.dll");

        for (var iteration = 0; iteration < 64; iteration++)
        {
            var component = new TestComponent(_ => { });
            Task<PiSharpExtensionUiCompletion<string>> operation;
            CancellationTokenSource? cancellation = null;
            if (iteration % 3 == 0)
            {
                operation = package.ShowCustomAsync<string>(_ => component);
                var escape = new TerminalInputEvent(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false), null);
                Assert.True(ui.InterceptTerminalInput(escape, reserved: false).Consumed);
            }
            else if (iteration % 3 == 1)
            {
                cancellation = new CancellationTokenSource();
                operation = package.ShowCustomAsync<string>(_ => component,
                    cancellationToken: cancellation.Token);
                await cancellation.CancelAsync();
            }
            else
            {
                operation = package.ShowCustomAsync<string>(_ => component);
                Assert.True(ui.InterceptTerminalInput(TerminalInputEvent.EndOfStream, reserved: false).Consumed);
            }

            try
            {
                if (cancellation is null)
                    Assert.True((await operation.WaitAsync(TimeSpan.FromSeconds(2))).IsCancelled);
                else
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            }
            finally { cancellation?.Dispose(); }
            Assert.Equal(1, component.DisposeCount);
        }

        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48);
            return frames.Count > 0 && !frames[^1].Screen.Contains("custom 48x16", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void DynamicWidgetRendersThroughTheScreenAndReplacementDisposesItsComponent()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        var factory = (IPiSharpExtensionUiPackageFactory)ui;
        using var package = factory.CreatePackageScope("widget.dll");
        var original = new RecordingWidget("first widget");

        package.SetWidget("dashboard", original);

        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("first widget", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2)));
        Assert.True(original.RenderCount > 0);
        Assert.True(original.ResizeCount > 0);

        var replacement = new RecordingWidget("replacement widget");
        package.SetWidget("dashboard", replacement);

        Assert.Equal(1, original.DisposeCount);
        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("replacement widget", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2)));

        package.Dispose();
        Assert.Equal(1, replacement.DisposeCount);
    }

    [Fact]
    public void DynamicComponentStatusSnapshotUsesStableStatusKeysAndLatestOwnerValue()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        var factory = (IPiSharpExtensionUiPackageFactory)ui;
        using var first = factory.CreatePackageScope("status-first.dll");
        using var second = factory.CreatePackageScope("status-second.dll");
        var component = new StatusAwareWidget();

        first.SetStatus("build", "first");
        second.SetStatus("build", "second");
        first.SetWidget("status-view", component);

        Assert.True(SpinWait.SpinUntil(() => component.BuildStatus == "second", TimeSpan.FromSeconds(2)));
        Assert.Equal("second", component.BuildStatus);
    }

    [Fact]
    public void DynamicWidgetRedrawBurstRendersTheLatestState()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("redraw-burst.dll");
        var widget = new StatefulRedrawWidget();
        package.SetWidget("redraw", widget);

        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("native-ui-redraw:0", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2)));
        const int updates = 200;
        for (var value = 1; value <= updates; value++) widget.SetValue(value);

        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48);
            return frames.Count > 0 && frames[^1].Screen.Contains($"native-ui-redraw:{updates}", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
        Assert.Equal(updates, widget.Value);
        Assert.True(widget.RenderCount > 0);
    }

    [Fact]
    public async Task ExtensionPresentationUpdatesRemainResponsiveDuringAssistantStreaming()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        var columns = 48;
        var rows = 16;
        using var screen = new TerminalScreen(output, error, () => Volatile.Read(ref columns), () => Volatile.Read(ref rows));
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        editor.Prefill("draft survives redraw");
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("streaming-ui.dll");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int updates = 200;
        var dark = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var light = TerminalThemeCatalog.LoadBuiltIn("light", TerminalColorMode.TrueColor);
        screen.AppendToolResult(PiSharpToolRenderView.FromText("collapsed tool result"),
            PiSharpToolRenderView.FromText(string.Join('\n', Enumerable.Range(0, 24).Select(index => $"tool-row:{index}"))));

        var stream = Task.Run(async () =>
        {
            await start.Task;
            for (var index = 0; index < updates; index++)
                screen.SetAssistantText($"assistant-stream-token:{index}");
        });
        var presentation = Task.Run(async () =>
        {
            await start.Task;
            for (var index = 0; index < updates; index++)
            {
                package.SetStatus("stream", $"status:{index}");
                package.SetWidget("stream", [$"native-ui-stream-update:{index}"]);
            }
        });
        var layout = Task.Run(async () =>
        {
            await start.Task;
            for (var index = 0; index < 40; index++)
            {
                Volatile.Write(ref columns, index % 2 == 0 ? 60 : 48);
                Volatile.Write(ref rows, index % 2 == 0 ? 20 : 16);
                screen.RefreshIfResized();
                screen.SetTheme(index % 2 == 0 ? light : dark);
                screen.ScrollPage(up: index % 2 == 0);
                screen.SetToolResultsExpanded(index % 2 == 0);
                screen.SetOverlay(["temporary modal"]);
                screen.SetOverlay(null);
            }
        });

        start.TrySetResult();
        await Task.WhenAll(stream, presentation, layout).WaitAsync(TimeSpan.FromSeconds(10));
        screen.ScrollToBottom();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48);
            if (frames.Count == 0) return false;
            var current = frames[^1].Screen;
            return current.Contains($"assistant-stream-token:{updates - 1}", StringComparison.Ordinal) &&
                current.Contains($"native-ui-stream-update:{updates - 1}", StringComparison.Ordinal) &&
                current.Contains($"status:{updates - 1}", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
        Assert.Equal("draft survives redraw", editor.Draft);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RepeatedRedrawRequestsFromRenderAreCoalescedAndStopAfterRemoval()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("self-redraw.dll");
        var widget = new SelfInvalidatingWidget();
        package.SetWidget("self-redraw", widget);

        await Task.Delay(TimeSpan.FromMilliseconds(250));
        var rendersWhileInstalled = widget.RenderCount;
        Assert.InRange(rendersWhileInstalled, 2, 32);

        package.ClearWidget("self-redraw");
        var rendersAfterRemoval = widget.RenderCount;
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.Equal(rendersAfterRemoval, widget.RenderCount);
    }

    [Fact]
    public void PackageOwnedKeysAndSingletonSlotsRestoreTheNextExtensionOnRemoval()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 60, () => 18);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        var factory = (IPiSharpExtensionUiPackageFactory)ui;
        using var first = factory.CreatePackageScope("first-ui.dll");
        using var second = factory.CreatePackageScope("second-ui.dll");

        first.SetStatus("shared", "first-status");
        first.SetWidget("shared", ["first-widget"]);
        first.SetHeader(new LabelComponent("first-header"));
        first.SetFooter(new LabelComponent("first-footer"));
        first.SetTitle("First title");
        second.SetStatus("shared", "second-status");
        second.SetWidget("shared", ["second-widget"], PiSharpExtensionWidgetPlacement.BelowEditor);
        second.SetHeader(new LabelComponent("second-header"));
        second.SetFooter(new LabelComponent("second-footer"));
        second.SetTitle("Second title");

        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("second-header", StringComparison.Ordinal) &&
            output.ToString().Contains("second-widget", StringComparison.Ordinal), TimeSpan.FromSeconds(2)));
        var overridden = TerminalOutputFrameReader.Read(output.ToString(), rows: 18, columns: 60)[^1].Screen;
        Assert.Contains("second-status", overridden);
        Assert.Contains("first-status", overridden);
        Assert.Contains("second-footer", overridden);
        Assert.DoesNotContain("first-footer", overridden);

        first.SetHeader(new LabelComponent("first-latest-header"));
        first.SetFooter(new LabelComponent("first-latest-footer"));
        first.SetTitle("First latest title");
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frame = TerminalOutputFrameReader.Read(output.ToString(), rows: 18, columns: 60)[^1].Screen;
            return frame.Contains("first-latest-header", StringComparison.Ordinal) &&
                frame.Contains("first-latest-footer", StringComparison.Ordinal) &&
                !frame.Contains("second-header", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
        Assert.Equal("First latest title", LastTerminalTitle(output.ToString()));

        second.Dispose();

        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frame = TerminalOutputFrameReader.Read(output.ToString(), rows: 18, columns: 60)[^1].Screen;
            return frame.Contains("first-latest-header", StringComparison.Ordinal) &&
                frame.Contains("first-widget", StringComparison.Ordinal) &&
                !frame.Contains("second-status", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
        var restored = TerminalOutputFrameReader.Read(output.ToString(), rows: 18, columns: 60)[^1].Screen;
        Assert.Contains("first-status", restored);
        Assert.DoesNotContain("second-status", restored);
        Assert.Contains("first-latest-header", restored);
        Assert.Contains("first-latest-footer", restored);
        Assert.DoesNotContain("second-footer", restored);
        Assert.DoesNotContain("second-widget", restored);
        Assert.Equal("First latest title", LastTerminalTitle(output.ToString()));

        static string LastTerminalTitle(string value)
        {
            var start = value.LastIndexOf("\u001b]0;", StringComparison.Ordinal);
            var end = value.IndexOf('\u0007', start);
            return start < 0 || end < 0 ? string.Empty : value[(start + 4)..end];
        }
    }

    [Fact]
    public void ReinstallingTheSameWidgetKeepsItsComponentAliveUntilRemoval()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("widget-reuse.dll");
        var component = new RecordingWidget("reused widget");

        package.SetWidget("dashboard", component);
        Assert.True(SpinWait.SpinUntil(() => component.RenderCount > 0, TimeSpan.FromSeconds(2)));
        package.SetWidget("dashboard", component, PiSharpExtensionWidgetPlacement.BelowEditor);

        Assert.Equal(0, component.DisposeCount);
        package.SetWidget("dashboard", (IPiSharpExtensionUiComponent?)null);
        Assert.Equal(1, component.DisposeCount);
    }

    [Fact]
    public void PackagePresentationControlsUseTheLiveEditorAndRestoreWorkingUiOnRemoval()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        editor.Prefill("draft");
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        var factory = (IPiSharpExtensionUiPackageFactory)ui;
        var package = factory.CreatePackageScope("presentation.dll");

        package.SetEditorText("expanded draft");
        Assert.Equal("expanded draft", package.GetEditorText());
        package.PasteToEditor(" + pasted");
        Assert.Equal("expanded draft + pasted", package.GetEditorText());

        screen.SetWorkingStatus("Working");
        package.SetWorkingMessage("Compiling extension");
        package.SetWorkingVisible(false);
        var hidden = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48)[^1].Screen;
        Assert.DoesNotContain("Compiling extension", hidden);
        package.SetWorkingVisible(true);
        package.SetWorkingIndicator(new(Array.Empty<string>()));
        var visible = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48)[^1].Screen;
        Assert.Contains("Compiling extension", visible);
        Assert.DoesNotContain("⠋", visible);
        package.SetWorkingIndicator(null);
        Assert.Throws<ArgumentOutOfRangeException>(() => package.SetWorkingIndicator(
            new(["a", "b"], TimeSpan.FromMilliseconds(1))));

        package.SetTitle("PiSharp ]52;clipboard dashboard");
        var titleOutput = output.ToString();
        var titleStart = titleOutput.LastIndexOf("\u001b]0;", StringComparison.Ordinal);
        var titleEnd = titleOutput.IndexOf('\u0007', titleStart);
        Assert.True(titleStart >= 0 && titleEnd > titleStart);
        var titlePayload = titleOutput[(titleStart + 4)..titleEnd];
        Assert.Equal("PiSharp ]52;clipboard dashboard", titlePayload);
        Assert.DoesNotContain('\u001b', titlePayload);

        package.Dispose();
        var restored = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48)[^1].Screen;
        Assert.Contains("Working", restored);
        Assert.DoesNotContain("Compiling extension", restored);
    }

    [Fact]
    public void WorkingPresentationUsesTheLatestSetterAndFallsBackAfterOwnerRemoval()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        var factory = (IPiSharpExtensionUiPackageFactory)ui;
        using var first = factory.CreatePackageScope("working-first.dll");
        using var second = factory.CreatePackageScope("working-second.dll");
        screen.SetWorkingStatus("Working");

        first.SetWorkingMessage("first message");
        second.SetWorkingMessage("second message");
        first.SetWorkingMessage("updated first message");
        second.SetWorkingIndicator(new(["S"]));
        first.SetWorkingIndicator(new(["F"]));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48);
            return frames.Count > 0 && frames[^1].Screen.Contains("F updated first message", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
        var latest = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48)[^1].Screen;
        Assert.Contains("updated first message", latest);
        Assert.DoesNotContain("second message", latest);
        Assert.Contains("F updated first message", latest);

        first.SetWorkingVisible(false);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48);
            return frames.Count > 0 && !frames[^1].Screen.Contains("updated first message", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
        var hidden = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48)[^1].Screen;
        Assert.DoesNotContain("updated first message", hidden);

        second.Dispose();
        var restored = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48)[^1].Screen;
        Assert.DoesNotContain("updated first message", restored);
        first.Dispose();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48);
            return frames.Count > 0 && frames[^1].Screen.Contains("Working", StringComparison.Ordinal) &&
                !frames[^1].Screen.Contains("updated first message", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
        var defaults = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48)[^1].Screen;
        Assert.Contains("Working", defaults);
        Assert.DoesNotContain("updated first message", defaults);
    }

    [Fact]
    public void ExtensionStatusAndTitleCollapseLineBreaksBeforeTerminalRendering()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("single-line-ui.dll");

        package.SetStatus("build\nbranch", "ready\nnow");
        package.SetTitle("Native\nDashboard");

        Assert.True(SpinWait.SpinUntil(() =>
        {
            var currentFrames = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48);
            return currentFrames.Count > 0 && currentFrames[^1].Screen.Split('\n')
                .Any(row => row.Contains("build branch: ready now", StringComparison.Ordinal));
        }, TimeSpan.FromSeconds(2)));
        var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48);
        Assert.Contains(frames[^1].Screen.Split('\n'), row => row.Contains("build branch: ready now", StringComparison.Ordinal));
        var captured = output.ToString();
        var titleStart = captured.LastIndexOf("\u001b]0;", StringComparison.Ordinal);
        var titleEnd = captured.IndexOf('\u0007', titleStart);
        Assert.True(titleStart >= 0 && titleEnd > titleStart);
        Assert.Equal("Native Dashboard", captured[(titleStart + 4)..titleEnd]);
    }

    [Fact]
    public async Task AutocompleteProvidersMergeWithNativeCompletionAndIgnoreDuplicatesAndFailures()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 60, () => 18);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        editor.Prefill("prefix rare-extension-query-48391");
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("autocomplete.dll");
        string? query = null;
        using var broken = package.AddAutocompleteProvider((_, _) =>
            throw new InvalidOperationException("expected provider failure"));
        using var registered = package.AddAutocompleteProvider((request, _) =>
        {
            query = request.Query;
            return ValueTask.FromResult<IReadOnlyList<PiSharpExtensionAutocompleteSuggestion>>(
            [
                new("Human label", "first-replacement"),
                new("Duplicate label", "first-replacement"),
                new("Second label", "second-replacement")
            ]);
        });
        var tab = new TerminalInputEvent(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false), null);

        await editor.HandleActiveInputAsync(tab, (_, _, _) => Task.FromResult(true), () => [], () => { });
        Assert.Equal("rare-extension-query-48391", query);
        Assert.Equal("prefix first-replacement", editor.Draft);
        Assert.Contains("Human label", output.ToString());
        Assert.DoesNotContain("Duplicate label", output.ToString());
        Assert.Contains("provider failed", error.ToString(), StringComparison.OrdinalIgnoreCase);

        await editor.HandleActiveInputAsync(tab, (_, _, _) => Task.FromResult(true), () => [], () => { });
        Assert.Equal("prefix second-replacement", editor.Draft);

        registered.Dispose();
        editor.Prefill("prefix rare-extension-query-48391");
        await editor.HandleActiveInputAsync(tab, (_, _, _) => Task.FromResult(true), () => [], () => { });
        Assert.Equal("prefix rare-extension-query-48391", editor.Draft);
    }

    [Fact]
    public async Task ThrowingAutocompleteCancellationStillRemovesPackageOwnedUi()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 60, () => 18);
        var editor = new TerminalEditor(commands: () => ["/thinking"]);
        editor.AttachScreen(screen);
        editor.Prefill("cancel-me");
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("throwing-cancel.dll");
        package.SetStatus("lifecycle", "should disappear");
        package.SetWidget("lifecycle", ["package cleanup survives cancellation errors"]);
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationCallbackInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerCancellationObserved = false;
        using var providerRegistration = package.AddAutocompleteProvider(async (request, cancellationToken) =>
        {
            using var throwingCallback = request.CancellationToken.Register(() =>
            {
                cancellationCallbackInvoked.TrySetResult();
                throw new InvalidOperationException("expected autocomplete cancellation failure");
            });
            providerStarted.TrySetResult();
            await releaseProvider.Task;
            providerCancellationObserved = cancellationToken.IsCancellationRequested;
            try { throw new InvalidOperationException("late autocomplete provider failure"); }
            finally { providerFinished.TrySetResult(); }
        });
        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains(
            "package cleanup survives cancellation errors", StringComparison.Ordinal), TimeSpan.FromSeconds(2)));

        var tab = new TerminalInputEvent(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false), null);
        var completion = editor.HandleActiveInputAsync(tab, (_, _, _) => Task.FromResult(true), () => [], () => { });
        bool callbackWasInvoked;
        try
        {
            await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            output.GetStringBuilder().Clear();
            package.Dispose();
            callbackWasInvoked = cancellationCallbackInvoked.Task.IsCompleted;
            await completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("cancel-me", editor.Draft);

            await editor.HandleActiveInputAsync(new(null, "x"), (_, _, _) => Task.FromResult(true),
                () => [], () => { });
            Assert.Equal("cancel-mex", editor.Draft);
        }
        finally { releaseProvider.TrySetResult(); }
        await providerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(callbackWasInvoked);
        Assert.True(providerCancellationObserved);
        Assert.Contains("autocomplete provider cancellation failed", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("package cleanup survives", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("should disappear", output.ToString(), StringComparison.Ordinal);

        editor.Prefill("/thi");
        await editor.HandleActiveInputAsync(tab, (_, _, _) => Task.FromResult(true), () => [], () => { });
        Assert.Equal("/thinking", editor.Draft);
    }

    [Fact]
    public async Task ConcurrentExtensionDraftPasteAndKeyboardEditsKeepEditorStateSerializable()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 60, () => 18);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        editor.Prefill("seed");
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("editor-race.dll");
        using var start = new Barrier(3);

        var setDraft = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(2)));
            package.SetEditorText("draft");
        });
        var paste = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(2)));
            package.PasteToEditor("!");
        });
        var keyboard = Task.Run(async () =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(2)));
            await editor.HandleActiveInputAsync(new(null, "x"), (_, _, _) => Task.FromResult(true),
                () => [], () => { });
        });

        await Task.WhenAll(setDraft, paste, keyboard).WaitAsync(TimeSpan.FromSeconds(5));

        var validFinalTexts = new[] { "draft", "draft!", "draftx", "draft!x", "draftx!" };
        var current = editor.CaptureDraftState();
        Assert.Contains(current.Text, validFinalTexts);
        Assert.Equal(current.Text.Length, current.Cursor);
        Assert.Null(current.SelectionStart);
        Assert.Null(current.SelectionEnd);

        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('-', ConsoleKey.OemMinus, false, false, true), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        var undone = editor.CaptureDraftState();
        Assert.Contains(undone.Text, new[] { "draft", "draft!", "draftx", "seed!x", "seedx!" });
        Assert.Equal(undone.Text.Length, undone.Cursor);
        Assert.Null(undone.SelectionStart);
        Assert.Null(undone.SelectionEnd);

        package.SetEditorText("select me");
        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: true, alt: false, control: false), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        var selected = editor.CaptureDraftState();
        Assert.Equal("select me", selected.Text);
        Assert.Equal(8, selected.Cursor);
        Assert.Equal(8, selected.SelectionStart);
        Assert.Equal(9, selected.SelectionEnd);

        package.PasteToEditor("!");
        var pasted = editor.CaptureDraftState();
        Assert.Equal("select m!", pasted.Text);
        Assert.Equal(pasted.Text.Length, pasted.Cursor);
        Assert.Null(pasted.SelectionStart);
        Assert.Null(pasted.SelectionEnd);
        await editor.HandleActiveInputAsync(new(null, "x"), (_, _, _) => Task.FromResult(true),
            () => [], () => { });
        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('-', ConsoleKey.OemMinus, false, false, true), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        Assert.Equal("select m!", editor.Draft);
        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('-', ConsoleKey.OemMinus, false, false, true), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        var restoredSelection = editor.CaptureDraftState();
        Assert.Equal("select me", restoredSelection.Text);
        Assert.Equal(8, restoredSelection.Cursor);
        Assert.Equal(8, restoredSelection.SelectionStart);
        Assert.Equal(9, restoredSelection.SelectionEnd);
    }

    [Fact]
    public async Task CustomEditorComponentOwnsInputAndRestoresPreviousRegistrationOnRemoval()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        editor.Prefill("draft");
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        var factory = (IPiSharpExtensionUiPackageFactory)ui;
        using var firstPackage = factory.CreatePackageScope("editor-first.dll");
        using var observer = factory.CreatePackageScope("editor-observer.dll");
        var first = new RecordingEditorComponent("first editor");
        firstPackage.SetEditorComponent(first);
        Assert.Same(first, firstPackage.GetEditorComponent());
        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("first editor", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2)));

        using var secondPackage = factory.CreatePackageScope("editor-second.dll");
        var second = new RecordingEditorComponent("second editor");
        secondPackage.SetEditorComponent(second);
        Assert.Same(second, firstPackage.GetEditorComponent());

        var firstUpdated = new RecordingEditorComponent("first updated editor");
        firstPackage.SetEditorComponent(firstUpdated);
        Assert.Same(firstUpdated, secondPackage.GetEditorComponent());

        var key = new TerminalInputEvent(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false), null);
        await editor.HandleActiveInputAsync(key, (_, _, _) => Task.FromResult(true), () => [], () => { });
        Assert.Equal(0, second.InputCount);
        Assert.Equal(1, firstUpdated.InputCount);
        Assert.Equal("draft", editor.Draft);

        secondPackage.Dispose();
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Same(firstUpdated, firstPackage.GetEditorComponent());
        await editor.HandleActiveInputAsync(key, (_, _, _) => Task.FromResult(true), () => [], () => { });
        Assert.Equal(2, firstUpdated.InputCount);
        firstPackage.Dispose();
        Assert.Equal(1, firstUpdated.DisposeCount);
        Assert.Null(observer.GetEditorComponent());
    }

    [Fact]
    public void ExtensionEditorCanReplaceAndRestoreTheBuiltInEditorWithoutLosingItsDraft()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        editor.SetDraft("preserved editor draft");
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("editor-replacement.dll");
        var replacement = new RecordingEditorComponent("native-ui-replacement-editor", replacesDefaultEditor: true);

        package.SetEditorComponent(replacement);

        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48);
            return frames.Count > 0 && frames[^1].Screen.Contains("native-ui-replacement-editor", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
        var replaced = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48)[^1].Screen;
        Assert.DoesNotContain("preserved editor draft", replaced, StringComparison.Ordinal);
        Assert.Equal("preserved editor draft", package.GetEditorText());

        package.SetEditorComponent(null);

        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 16, columns: 48);
            return frames.Count > 0 && frames[^1].Screen.Contains("preserved editor draft", StringComparison.Ordinal) &&
                !frames[^1].Screen.Contains("native-ui-replacement-editor", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, replacement.DisposeCount);
    }

    [Fact]
    public async Task MultilineEditUsesTheHostEditorAndRestoresTheOriginalDraftOnSaveOrEscape()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 60, () => 18);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        editor.Prefill("saved draft");
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("edit.dll");

        editor.AttachTerminalInput(new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("x\u001b[13;5u"))));
        var save = package.EditAsync("Edit extension text", "seed");
        Assert.Equal("seedx", await save.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("saved draft", editor.Draft);

        editor.AttachTerminalInput(new TerminalInput(new MemoryStream([27])));
        var cancel = package.EditAsync("Edit extension text", "temporary");
        Assert.Null(await cancel.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("saved draft", editor.Draft);

    }

    [Fact]
    public async Task HeadlessUiOperationsReturnConsistentNoUiResults()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        var ui = new ProjectTrustStartupUi(false, "print", output, error);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("headless.dll");

        Assert.Null(await package.SelectAsync("Select", ["one"]).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await package.ConfirmAsync("Confirm", "Message").WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(await package.InputAsync("Input").WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(await package.EditAsync("Edit").WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True((await package.ShowCustomAsync<string>(_ => throw new InvalidOperationException("factory should not run")))
            .IsCancelled);
        Assert.False(package.SetTheme("unknown").Success);
        Assert.Empty(package.GetAllThemes());

        foreach (var mode in new[] { "rpc", "json", "print", "direct" })
        {
            var modeUi = new ProjectTrustStartupUi(false, mode, output, error);
            using var modePackage = ((IPiSharpExtensionUiPackageFactory)modeUi).CreatePackageScope(mode + ".dll");
            Assert.Null(await modePackage.SelectAsync("Select", ["one"]).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(await modePackage.ConfirmAsync("Confirm", "Message").WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Null(await modePackage.InputAsync("Input").WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True((await modePackage.ShowCustomAsync<string>(_ =>
                throw new InvalidOperationException("headless component factory should not run"))).IsCancelled);
        }
    }

    [Fact]
    public async Task CustomComponentFactoryRacingScreenShutdownReturnsCancelled()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        using var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("custom-shutdown-race.dll");
        RecordingWidget? component = null;

        var result = await package.ShowCustomAsync<string>(_ =>
        {
            binding.Dispose();
            return component = new RecordingWidget("unhosted component");
        }).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.IsCancelled);
        Assert.NotNull(component);
        Assert.Equal(1, component.DisposeCount);
    }

    [Fact]
    public async Task CustomComponentFactoryRacingPackageRemovalCancelsAndDisposesBeforeInstalling()
    {
        using var output = new SynchronizedStringWriter();
        using var error = new SynchronizedStringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 16);
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen);
        var package = ((IPiSharpExtensionUiPackageFactory)ui).CreatePackageScope("custom-package-race.dll");
        var component = new RecordingWidget("removed before installation");

        var operation = package.ShowCustomAsync<string>(_ =>
        {
            package.Dispose();
            return component;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, component.DisposeCount);
        Assert.DoesNotContain("removed before installation", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedConfigureRollsBackPackageOwnedUiRegistrations()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-native-ui-rollback-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        try
        {
            var repositoryRoot = FindRepositoryRoot();
            var fixtureDirectory = Path.Combine(repositoryRoot, "tests", "fixtures",
                "NativeExtensionInteractiveUiFixture", "bin", BuildConfiguration(), "net10.0");
            var fixturePackage = CreateFixturePackage(root, fixtureDirectory,
                "PiSharp.NativeExtensionInteractiveUiFixture.FailingConfigureUiExtension");
            var packageStore = new NativeExtensionPackageStore(agentDirectory);
            var installed = await packageStore.InstallAsync(root, fixturePackage, "user");
            var packageRoots = await packageStore.ResolveAsync(root, trusted: true, userSources: [installed]);

            using var output = new SynchronizedStringWriter();
            using var error = new SynchronizedStringWriter();
            using var screen = new TerminalScreen(output, error, () => 80, () => 24);
            var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
            using var binding = ui.BindInteractiveScreen(screen);
            using var catalog = ExtensionCatalog.Load(agentDirectory, root, projectTrusted: true,
                nativeExtensionPackageRoots: packageRoots, uiPackageFactory: ui);

            Assert.Empty(catalog.LoadedExtensions);
            Assert.Contains(catalog.Warnings, warning => warning.Warning.Contains(
                "expected UI configure failure", StringComparison.Ordinal));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 24, columns: 80);
                return frames.Count > 0 && !frames[^1].Screen.Contains("must be removed", StringComparison.Ordinal) &&
                    !frames[^1].Screen.Contains("rollback-widget", StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task RemovingCompiledPackageReleasesItsUiCallbacksAndCollectibleLoadContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-native-ui-alc-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        try
        {
            var repositoryRoot = FindRepositoryRoot();
            var fixtureDirectory = Path.Combine(repositoryRoot, "tests", "fixtures",
                "NativeExtensionInteractiveUiFixture", "bin", BuildConfiguration(), "net10.0");
            var fixturePackage = CreateFixturePackage(root, fixtureDirectory);
            var packageStore = new NativeExtensionPackageStore(agentDirectory);
            var installed = await packageStore.InstallAsync(root, fixturePackage, "user");
            var packageRoots = await packageStore.ResolveAsync(root, trusted: true, userSources: [installed]);

            using var output = new SynchronizedStringWriter();
            using var error = new SynchronizedStringWriter();
            using var screen = new TerminalScreen(output, error, () => 80, () => 24);
            var editor = new TerminalEditor();
            editor.AttachScreen(screen);
            for (var cycle = 0; cycle < 5; cycle++)
            {
                var loadContext = await LoadAndRemoveUiPackageAsync(agentDirectory, root, packageRoots,
                    screen, editor, output, error);

                Assert.True(SpinWait.SpinUntil(() =>
                {
                    var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 24, columns: 80);
                    return frames.Count > 0 && !frames[^1].Screen.Contains("native-ui-widget:", StringComparison.Ordinal);
                }, TimeSpan.FromSeconds(2)));
                AssertContextUnloaded(loadContext);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<WeakReference> LoadAndRemoveUiPackageAsync(string agentDirectory, string workingDirectory,
        IReadOnlyList<NativeExtensionPackageRoot> packageRoots, TerminalScreen screen, TerminalEditor editor,
        SynchronizedStringWriter output, TextWriter error)
    {
        var ui = new ProjectTrustStartupUi(true, "interactive", output, error);
        using var binding = ui.BindInteractiveScreen(screen, editor);
        var packageFactory = new TrackingUiPackageFactory(ui);
        var active = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: packageRoots, uiPackageFactory: packageFactory);
        var loadContext = Assert.Single(active.NativePackageLoadContextReferences);
        var packageUi = Assert.IsAssignableFrom<IPiSharpExtensionUiPackageScope>(packageFactory.LastScope);
        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("native-ui-widget:0:idle", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2)));

        var removed = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            preloaded: active, nativeExtensionPackageRoots: []);
        Assert.Empty(removed.LoadedExtensions);
        Assert.Throws<ObjectDisposedException>(() => packageUi.SetStatus("after-removal", "must not render"));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 24, columns: 80);
            return frames.Count > 0 && !frames[^1].Screen.Contains("native-ui-widget:", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2)));
        active.Dispose();
        Assert.Empty(removed.NativePackageLoadContextReferences);
        removed.Dispose();
        return Task.FromResult(loadContext);
    }

    private sealed class TrackingUiPackageFactory(IPiSharpExtensionUiPackageFactory inner)
        : IPiSharpExtensionUiPackageFactory
    {
        public IPiSharpExtensionUiPackageScope? LastScope { get; private set; }

        public IPiSharpExtensionUiPackageScope CreatePackageScope(string sourcePath) =>
            LastScope = inner.CreatePackageScope(sourcePath);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertContextUnloaded(WeakReference context)
    {
        for (var attempt = 0; attempt < 20 && context.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(context.IsAlive, "A removed extension UI package remained reachable.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find PiSharp.slnx.");
    }

    private static string BuildConfiguration() =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";

    private static string CreateFixturePackage(string root, string fixtureDirectory,
        string extensionType = "PiSharp.NativeExtensionInteractiveUiFixture.InteractiveUiExtension")
    {
        var packagePath = Path.Combine(root, "native-ui-fixture.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("pisharp-extension.json").Open()))
            writer.Write(JsonSerializer.Serialize(new
            {
                id = "PiSharp.NativeExtensionInteractiveUiFixture",
                version = "1.0.0",
                apiVersion = 1,
                extensions = new[]
                {
                    new
                    {
                        assembly = "lib/net10.0/PiSharp.NativeExtensionInteractiveUiFixture.dll",
                        type = extensionType
                    }
                }
            }));

        foreach (var fileName in new[]
                 {
                     "PiSharp.NativeExtensionInteractiveUiFixture.dll",
                     "PiSharp.NativeExtensionInteractiveUiFixture.deps.json"
                 })
            archive.CreateEntryFromFile(Path.Combine(fixtureDirectory, fileName), "lib/net10.0/" + fileName);
        return packagePath;
    }

    private sealed class TestComponent(Action<string> complete) : IPiSharpExtensionUiComponent
    {
        private int _handleCount;
        private int _disposeCount;
        private int _resizeCount;
        public int HandleCount => Volatile.Read(ref _handleCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int ResizeCount => Volatile.Read(ref _resizeCount);
        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context) =>
            [new($"custom {context.Width}x{context.Height}", "accent")];
        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input)
        {
            Interlocked.Increment(ref _handleCount);
            complete("submitted");
            complete("ignored second completion");
            return PiSharpExtensionUiInputDisposition.Handled;
        }
        public void OnResize(PiSharpExtensionUiSize size) => Interlocked.Increment(ref _resizeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class RecordingWidget(string text) : IPiSharpExtensionUiComponent
    {
        private int _renderCount;
        private int _resizeCount;
        private int _disposeCount;
        private Action? _redraw;
        public int RenderCount => Volatile.Read(ref _renderCount);
        public int ResizeCount => Volatile.Read(ref _resizeCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context)
        {
            Interlocked.Increment(ref _renderCount);
            return [new($"{text} {context.Width}", "success")];
        }
        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;
        public void AttachRedraw(Action requestRedraw) => Volatile.Write(ref _redraw, requestRedraw);
        public void RequestRedraw() => Volatile.Read(ref _redraw)?.Invoke();
        public void OnResize(PiSharpExtensionUiSize size) => Interlocked.Increment(ref _resizeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class StatusAwareWidget : IPiSharpExtensionUiComponent
    {
        private string? _buildStatus;

        public string? BuildStatus => Volatile.Read(ref _buildStatus);

        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context)
        {
            Volatile.Write(ref _buildStatus, context.Statuses.GetValueOrDefault("build"));
            return [new($"Build: {BuildStatus ?? "none"}")];
        }

        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;

        public void Dispose() { }
    }

    private sealed class BlockingRenderWidget(ManualResetEventSlim entered, ManualResetEventSlim release)
        : IPiSharpExtensionUiComponent
    {
        private readonly TaskCompletionSource _renderReturned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RenderReturned => _renderReturned.Task;

        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context)
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(2));
            _renderReturned.TrySetResult();
            return [new("stale widget")];
        }

        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;

        public void Dispose() { }
    }

    private sealed class StatefulRedrawWidget : IPiSharpExtensionUiComponent
    {
        private readonly object _gate = new();
        private Action? _redraw;
        private int _value;
        private int _renderCount;

        public int Value { get { lock (_gate) return _value; } }
        public int RenderCount => Volatile.Read(ref _renderCount);

        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context)
        {
            Interlocked.Increment(ref _renderCount);
            lock (_gate) return [new($"native-ui-redraw:{_value}")];
        }

        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;

        public void AttachRedraw(Action requestRedraw) => Volatile.Write(ref _redraw, requestRedraw);

        public void SetValue(int value)
        {
            lock (_gate) _value = value;
            Volatile.Read(ref _redraw)?.Invoke();
        }

        public void Dispose() { }
    }

    private sealed class SelfInvalidatingWidget : IPiSharpExtensionUiComponent
    {
        private Action? _redraw;
        private int _renderCount;

        public int RenderCount => Volatile.Read(ref _renderCount);

        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context)
        {
            Interlocked.Increment(ref _renderCount);
            Volatile.Read(ref _redraw)?.Invoke();
            return [new("self-redraw")];
        }

        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;

        public void AttachRedraw(Action requestRedraw) => Volatile.Write(ref _redraw, requestRedraw);

        public void Dispose() => Volatile.Write(ref _redraw, null);
    }

    private sealed class RecordingEditorComponent(string text, bool replacesDefaultEditor = false)
        : IPiSharpExtensionEditorComponent
    {
        private int _inputCount;
        private int _disposeCount;
        public int InputCount => Volatile.Read(ref _inputCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool ReplacesDefaultEditor => replacesDefaultEditor;
        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context) =>
            [new(text, "accent")];
        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input)
        {
            Interlocked.Increment(ref _inputCount);
            return PiSharpExtensionUiInputDisposition.Handled;
        }
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class CompletingComponent : IPiSharpExtensionUiComponent
    {
        private Action<string>? _complete;
        private bool _disposed;

        public bool WasDisposedInsideInput { get; private set; }
        public bool IsDisposed => _disposed;
        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context) => [];

        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input)
        {
            _complete!("completed");
            WasDisposedInsideInput = _disposed;
            return PiSharpExtensionUiInputDisposition.Handled;
        }

        public void Dispose() => _disposed = true;
        public void SetCompletion(Action<string> complete) => _complete = complete;
    }

    private sealed class ThrowingRenderComponent : IPiSharpExtensionUiComponent
    {
        private int _rendering;
        private int _renderCount;
        private int _disposeCount;

        public int RenderCount => Volatile.Read(ref _renderCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool WasDisposedInsideRender { get; private set; }

        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context)
        {
            Interlocked.Increment(ref _renderCount);
            Volatile.Write(ref _rendering, 1);
            try { throw new InvalidOperationException("expected custom render failure"); }
            finally { Volatile.Write(ref _rendering, 0); }
        }

        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;

        public void Dispose()
        {
            WasDisposedInsideRender = Volatile.Read(ref _rendering) != 0;
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class CompletingOnResizeComponent : IPiSharpExtensionUiComponent
    {
        private Action<string>? _complete;
        private int _resizing;
        private int _renderCount;
        private int _disposeCount;

        public int RenderCount => Volatile.Read(ref _renderCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool WasDisposedInsideResize { get; private set; }

        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context)
        {
            Interlocked.Increment(ref _renderCount);
            return [new("should not render after completion")];
        }

        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;

        public void OnResize(PiSharpExtensionUiSize size)
        {
            Volatile.Write(ref _resizing, 1);
            try { _complete!("resized"); }
            finally { Volatile.Write(ref _resizing, 0); }
        }

        public void Dispose()
        {
            WasDisposedInsideResize = Volatile.Read(ref _resizing) != 0;
            Interlocked.Increment(ref _disposeCount);
        }

        public void SetCompletion(Action<string> complete) => _complete = complete;
    }

    private sealed class LabelComponent(string text) : IPiSharpExtensionUiComponent
    {
        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context) => [new(text)];
        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;
        public void Dispose() { }
    }

    private sealed class MouseTrackingComponent : IPiSharpExtensionUiComponent
    {
        public PiSharpExtensionUiMouseEvent? Mouse { get; private set; }

        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context) => [];

        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input)
        {
            Mouse = input.Mouse;
            return PiSharpExtensionUiInputDisposition.Handled;
        }

        public void Dispose() { }
    }

    private sealed class SynchronizedStringWriter : StringWriter
    {
        private readonly object _gate = new();

        public override void Write(char value)
        {
            lock (_gate) base.Write(value);
        }

        public override void Write(string? value)
        {
            lock (_gate) base.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate) base.Write(buffer, index, count);
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            lock (_gate) base.Write(buffer);
        }

        public override void WriteLine(string? value)
        {
            lock (_gate) base.WriteLine(value);
        }

        public override string ToString()
        {
            lock (_gate) return base.ToString();
        }
    }
}
