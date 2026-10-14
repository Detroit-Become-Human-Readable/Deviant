using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using Xunit;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class ViewportFlightTests
{
    [Fact]
    public void StrafeKeyIsNotAlsoABottomViewShortcut() => Run(() =>
    {
        var view = new AssetViewport();
        var renderer = (Viewport3DX)view.FindName("RenderView");
        Assert.DoesNotContain(renderer.InputBindings.OfType<KeyBinding>(), binding =>
            binding.Key == Key.D && binding.Modifiers == ModifierKeys.None &&
            binding.Command == ViewportCommands.BottomView);
    });

    [Fact]
    public void OrdinaryKeyHandlingIsPreservedWhenFlightIsDisabled() => Run(() =>
    {
        var view = new AssetViewport();
        foreach (var key in new[] { Key.W, Key.A, Key.S, Key.D, Key.Q, Key.E })
        {
            var args = KeyArgs(key);
            Invoke(view, "KeyDownHandler", view, args);
            Assert.False(args.Handled);
        }
        Assert.Empty(Field<HashSet<Key>>(view, "flyKeys"));
    });

    [Fact]
    public void RoutedStrafeInputIsHandledWithoutChangingOrientation() => Run(() =>
    {
        var view = FlightView();
        var renderer = (Viewport3DX)view.FindName("RenderView");
        var camera = Camera(view);
        var look = camera.LookDirection;
        var up = camera.UpDirection;
        var key = KeyArgs(Key.D);
        renderer.RaiseEvent(key);
        Assert.True(key.Handled);
        key.RoutedEvent = Keyboard.KeyDownEvent;
        renderer.RaiseEvent(key);
        Set(view, "lastFlyTime", Field<Stopwatch>(view, "flyClock").Elapsed.TotalSeconds - .05);
        Invoke(view, "FlyTick", null, EventArgs.Empty);
        Assert.True(camera.Position.X > 0);
        Assert.Equal(look, camera.LookDirection);
        Assert.Equal(up, camera.UpDirection);
        Invoke(view, "StopFlight");
    });

    [Theory]
    [InlineData(Key.W, 0, 0, -1)]
    [InlineData(Key.S, 0, 0, 1)]
    [InlineData(Key.A, -1, 0, 0)]
    [InlineData(Key.D, 1, 0, 0)]
    public void HeldKeyTranslatesWithoutRotating(Key key, int x, int y, int z) => Run(() =>
    {
        var view = FlightView();
        var camera = Camera(view);
        var look = camera.LookDirection;
        Invoke(view, "KeyDownHandler", view, KeyArgs(key));
        Set(view, "lastFlyTime", Field<Stopwatch>(view, "flyClock").Elapsed.TotalSeconds - .05);
        Invoke(view, "FlyTick", null, EventArgs.Empty);
        var distance = camera.Position - new Point3D();
        distance.Normalize();
        Assert.True((distance - new Vector3D(x, y, z)).Length < 1e-6);
        Assert.Equal(look, camera.LookDirection);
        Assert.Equal(new Vector3D(0, 1, 0), camera.UpDirection);
        Invoke(view, "StopFlight");
    });

    [Fact]
    public void RepeatedKeyDownDoesNotDiscardElapsedMovement() => Run(() =>
    {
        var view = FlightView();
        var camera = Camera(view);
        Invoke(view, "KeyDownHandler", view, KeyArgs(Key.W));
        Set(view, "lastFlyTime", Field<Stopwatch>(view, "flyClock").Elapsed.TotalSeconds - .05);
        Invoke(view, "KeyDownHandler", view, KeyArgs(Key.W));
        Invoke(view, "FlyTick", null, EventArgs.Empty);
        Assert.InRange(-camera.Position.Z, .0059, .0061);
        Invoke(view, "StopFlight");
    });

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void LargePitchDragCannotCrossAVerticalPole(int sign) => Run(() =>
    {
        var view = FlightView();
        var renderer = (Viewport3DX)view.FindName("RenderView");
        var args = new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.PreviewMouseMoveEvent };
        var pointer = args.GetPosition(renderer);
        Set(view, "flyDragging", true);
        Set(view, "flyPointer", new Point(pointer.X, pointer.Y - sign * Math.PI / .004));
        Invoke(view, "FlyMove", renderer, args);
        var camera = Camera(view);
        Assert.True(camera.LookDirection.Z < 0, "Pitch must keep the original horizontal heading.");
        Assert.InRange(Math.Abs(camera.LookDirection.Y / camera.LookDirection.Length), 0, .995001);
        Assert.Equal(new Vector3D(0, 1, 0), camera.UpDirection);
        Invoke(view, "StopFlight");
    });

    [Fact]
    public void FocusLossClearsHeldKeysAndTimer() => Run(() =>
    {
        var view = FlightView();
        Invoke(view, "KeyDownHandler", view, KeyArgs(Key.W));
        view.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, view, null)
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        Assert.Empty(Field<HashSet<Key>>(view, "flyKeys"));
        Assert.False(Field<System.Windows.Threading.DispatcherTimer>(view, "flyTimer").IsEnabled);
        Assert.False(Field<bool>(view, "flyDragging"));
    });

    [Fact]
    public void EnteringFlightRestoresUprightCameraFromAVerticalPreset() => Run(() =>
    {
        var view = new AssetViewport();
        var camera = Camera(view);
        camera.LookDirection = new Vector3D(0, 4, 0);
        camera.UpDirection = new Vector3D(0, 0, 1);
        ((ToggleButton)view.FindName("FlyToggle")).IsChecked = true;
        Assert.Equal(new Vector3D(0, 1, 0), camera.UpDirection);
        Assert.InRange(camera.LookDirection.Length, 3.99999, 4.00001);
        Assert.InRange(camera.LookDirection.Y / camera.LookDirection.Length, 0, .995001);
        Assert.True(camera.LookDirection.Z < 0);
    });

    [Fact]
    public void LeavingFlightStopsMovementAndPreservesOtherGestures() => Run(() =>
    {
        var view = FlightView();
        var renderer = (Viewport3DX)view.FindName("RenderView");
        Assert.Contains(renderer.InputBindings.OfType<MouseBinding>(), binding =>
            binding.Command == ViewportCommands.Rotate);
        Assert.Contains(renderer.InputBindings.OfType<KeyBinding>(), binding =>
            binding.Key == Key.U && binding.Command == ViewportCommands.TopView);
        Invoke(view, "KeyDownHandler", view, KeyArgs(Key.W));
        ((ToggleButton)view.FindName("FlyToggle")).IsChecked = false;
        Assert.Empty(Field<HashSet<Key>>(view, "flyKeys"));
        Assert.False(Field<System.Windows.Threading.DispatcherTimer>(view, "flyTimer").IsEnabled);
        var arrow = KeyArgs(Key.Left);
        Invoke(view, "KeyDownHandler", view, arrow);
        Assert.False(arrow.Handled);
    });

    private static AssetViewport FlightView()
    {
        var view = new AssetViewport();
        ((ToggleButton)view.FindName("FlyToggle")).IsChecked = true;
        var camera = Camera(view);
        camera.Position = new Point3D(); camera.LookDirection = new Vector3D(0, 0, -1);
        camera.UpDirection = new Vector3D(0, 1, 0);
        return view;
    }
    private static PerspectiveCamera Camera(AssetViewport view) =>
        (PerspectiveCamera)((Viewport3DX)view.FindName("RenderView")).Camera!;
    private static KeyEventArgs KeyArgs(Key key) => new(Keyboard.PrimaryDevice, new TestSource(), 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
    private static T Field<T>(object value, string name) => (T)value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Set(object value, string name, object data) => value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, data);
    private static void Invoke(object value, string name, params object?[] args) => value.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, args);
    private sealed class TestSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
