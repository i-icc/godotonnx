using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using GodotOnnx.Godot;
using GodotOnnx.Inference;

namespace Demo;

/// <summary>
/// ローカル AIVMX TTS デモの UI。全 UI はコードで構築する。
/// </summary>
public partial class Demo : Control
{
    record HistoryEntry(string Text, int StyleId, float Weight, float Speed);

    TtsSpeaker speaker = null!;

    // UI 参照
    Label statusLabel = null!;
    Label queueLabel = null!;
    List<Button> styleButtons = [];
    HSlider speedSlider = null!;
    HSlider weightSlider = null!;
    Label speedValue = null!;
    Label weightValue = null!;
    TextEdit input = null!;
    Button speakButton = null!;
    Button stopButton = null!;
    ItemList historyList = null!;
    readonly List<HistoryEntry> history = [];

    // テーマ色
    static readonly Color BgColor = new("171a21");
    static readonly Color PanelColor = new("20242e");
    static readonly Color AccentColor = new("7aa2f7");
    static readonly Color FgColor = new("c8cdd8");
    static readonly Color MutedColor = new("8a92a5");
    static readonly Color OkColor = new("9ece6a");
    static readonly Color WarnColor = new("e0af68");
    static readonly Color ErrorColor = new("f7768e");

    public override void _Ready()
    {
        BuildUi();

        speaker = new TtsSpeaker();
        AddChild(speaker);
        speaker.StateChanged += OnSpeakerStateChanged;
        speaker.PlaybackStarted += OnPlaybackStarted;
        speaker.QueueChanged += OnQueueChanged;

        _ = InitializeAsync(smokeTest: OS.GetEnvironment("TTS_DEMO_SMOKE") == "1");
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        // Esc: 再生/合成をすべて停止しておく (TextEdit にフォーカスがあっても届くように unhandled で受ける)
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            speaker.StopAll();
            GetViewport().SetInputAsHandled();
        }
    }

    async Task InitializeAsync(bool smokeTest = false)
    {
        var paths = TtsPaths.Resolve();
        if (paths is null)
        {
            UpdateStatus(ErrorColor,
                "モデルが見つかりません (GodotOnnx.sln があるディレクトリの models/ に配置してください)");
            GD.PrintErr(TtsPaths.DescribeMissing());
            return;
        }

        await speaker.InitializeAsync(paths.Aivmx, paths.BertModel, paths.Tokenizer, paths.NaistJdic);
        // 完了通知は TtsSpeaker がシグナルで送る

        if (smokeTest)
        {
            await RunSmokeTest();
        }
    }

    /// <summary>
    /// ヘッドレス検証用: 初期化完了後に 1 発話 → 数秒待って終了する。
    /// </summary>
    async Task RunSmokeTest()
    {
        var tree = GetTree();
        await ToSignal(tree.CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
        var styleNames = speaker.StyleNames;
        if (styleNames.Count == 0 || speaker.CurrentState != SpeakerState.Ready)
        {
            GD.Print("[Smoke] NG: engine not ready");
            tree.Quit(1);
            return;
        }

        // UI の状態確認用スクリーンショット (TTS_DEMO_SHOT にパス指定時)
        var shotPath = OS.GetEnvironment("TTS_DEMO_SHOT");
        if (shotPath.Length > 0)
        {
            await ToSignal(tree.CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            var img = GetViewport().GetTexture().GetImage();
            img.SavePng(shotPath);
            GD.Print($"[Smoke] screenshot -> {shotPath}");
        }

        var speakAt = Time.GetTicksMsec();
        speaker.Speak("こんにちは。ローカルの音声合成デモへようこそ。", 0, 1f, 1f);
        GD.Print($"[Smoke] Speak() issued at t={speakAt}");
        await ToSignal(tree.CreateTimer(8.0), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Smoke] exit, state={speaker.CurrentState}");

        // 再生後 UI の確認用 (TTS_DEMO_SHOT2 にパス指定時)
        var shotPath2 = OS.GetEnvironment("TTS_DEMO_SHOT2");
        if (shotPath2.Length > 0)
        {
            GetViewport().GetTexture().GetImage().SavePng(shotPath2);
            GD.Print($"[Smoke] screenshot -> {shotPath2}");
        }
        tree.Quit();
    }

    // ------------------------------------------------------------------ UI 構築

    void BuildUi()
    {
        var root = new PanelContainer
        {
            Name = "RootPanel",
        };
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var panelBox = new StyleBoxFlat
        {
            BgColor = BgColor,
            ContentMarginLeft = 0,
        };
        root.AddThemeStyleboxOverride("panel", panelBox);
        AddChild(root);

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 26);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_right", 26);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        root.AddChild(margin);

        var vbox = new VBoxContainer().With(c => c.AddThemeConstantOverride("separation", 12));
        margin.AddChild(vbox);

        // ヘッダー
        var header = new HBoxContainer();
        vbox.AddChild(header);
        var title = new Label
        {
            Text = "🎤 Local AIVMX TTS",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        title.AddThemeFontSizeOverride("font_size", 21);
        title.AddThemeColorOverride("font_color", FgColor);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);
        statusLabel = MakeLabel(MutedColor, "初期化中…", 14);
        statusLabel.HorizontalAlignment = HorizontalAlignment.Right;
        header.AddChild(statusLabel);

        vbox.AddChild(MakeHSeparator());

        // スタイル選択
        var styleRow = new HBoxContainer().With(c => c.AddThemeConstantOverride("separation", 10));
        vbox.AddChild(styleRow);
        styleRow.AddChild(MakeLabel(FgColor, "スタイル"));
        var buttonGroup = new ButtonGroup();
        string[] styleNameCandidates = ["ノーマル", "あまあま", "せつなめ", "ねむたい"];
        for (var i = 0; i < 4; i++)
        {
            var b = new Button
            {
                Text = styleNameCandidates[i],
                ToggleMode = true,
                ButtonPressed = i == 0,
                ButtonGroup = buttonGroup,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(96, 34),
            };
            // 押下状態の見た目は "pressed" スタイルボックス (アクセント色) が自動で担当する
            StyleButton(b, AccentColor);
            styleRow.AddChild(b);
            styleButtons.Add(b);
        }
        styleRow.AddChild(MakeSpacer());

        // スライダー (話速 / スタイル重み)
        var grid = new GridContainer().With(c =>
        {
            c.Columns = 3;
            c.AddThemeConstantOverride("h_separation", 14);
        });
        vbox.AddChild(grid);
        grid.AddChild(MakeLabel(FgColor, "話速"));
        speedSlider = MakeSlider(0.5f, 2.0f, 0.05f, 1.0f);
        grid.AddChild(speedSlider);
        speedValue = MakeLabel(AccentColor, "1.00x", 14);
        grid.AddChild(RightWrap(speedValue));

        grid.AddChild(MakeLabel(FgColor, "スタイル重み"));
        weightSlider = MakeSlider(0.0f, 2.0f, 0.05f, 1.0f);
        grid.AddChild(weightSlider);
        weightValue = MakeLabel(AccentColor, "1.00", 14);
        grid.AddChild(RightWrap(weightValue));

        speedSlider.ValueChanged += _ => speedValue.Text = $"{speedSlider.Value:0.00}x";
        weightSlider.ValueChanged += _ => weightValue.Text = $"{weightSlider.Value:0.00}";

        // テキスト入力
        input = new TextEdit
        {
            PlaceholderText = "話す内容を入力… (Ctrl+Enter で発話 / Esc で停止)",
            CustomMinimumSize = new Vector2(0, 120),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
        };
        input.AddThemeStyleboxOverride("normal", MakePanelStyle(PanelColor, 8, 2));
        input.AddThemeStyleboxOverride("focus", MakePanelStyle(PanelColor, 8, 2, AccentColor));
        input.GuiInput += e =>
        {
            if (e is InputEventKey key && key.Pressed && key.CtrlPressed
                && key.Keycode is Key.Enter or Key.KpEnter)
            {
                Speak();
                GetViewport().SetInputAsHandled();
            }
        };
        vbox.AddChild(input);

        // 操作ボタン
        var buttonRow = new HBoxContainer().With(c => c.AddThemeConstantOverride("separation", 12));
        vbox.AddChild(buttonRow);
        speakButton = new Button
        {
            Text = "♪ 話す",
            CustomMinimumSize = new Vector2(130, 40),
            FocusMode = FocusModeEnum.None,
        };
        StyleButton(speakButton, AccentColor, textDark: true);
        speakButton.Pressed += Speak;
        speakButton.Disabled = true;
        buttonRow.AddChild(speakButton);

        stopButton = new Button
        {
            Text = "■ 停止",
            CustomMinimumSize = new Vector2(96, 40),
            FocusMode = FocusModeEnum.None,
        };
        stopButton.AddThemeColorOverride("font_color", ErrorColor);
        stopButton.Pressed += () => speaker.StopAll();
        buttonRow.AddChild(stopButton);

        buttonRow.AddChild(MakeSpacer());
        queueLabel = MakeLabel(MutedColor, "キュー 0 / 0 件", 13);
        queueLabel.HorizontalAlignment = HorizontalAlignment.Right;
        buttonRow.AddChild(queueLabel);

        // 履歴
        var historyHead = new HBoxContainer();
        vbox.AddChild(historyHead);
        historyHead.AddChild(MakeLabel(FgColor, "履歴"));
        historyHead.AddChild(MakeSpacer());
        historyHead.AddChild(MakeLabel(MutedColor, "ダブルクリックでもう一度話す", 12));
        historyList = new ItemList
        {
            CustomMinimumSize = new Vector2(0, 150),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        historyList.AddThemeStyleboxOverride("panel", MakePanelStyle(PanelColor, 8, 2));
        historyList.ItemActivated += OnHistoryActivated;
        vbox.AddChild(historyList);

        // フッター
        var footer = new Label
        {
            Text = "コハク (AIVMX / Style-Bert-VITS2 JP-Extra) ・ ONNX Runtime ローカル推論 ・ 音声は実行毎に微妙に変化します",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        footer.AddThemeFontSizeOverride("font_size", 12);
        footer.AddThemeColorOverride("font_color", MutedColor);
        vbox.AddChild(footer);
    }

    // ------------------------------------------------------------------ 操作

    void Speak()
    {
        var text = input.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }
        var styleId = SelectedStyleId();
        speaker.Speak(text, styleId, (float)weightSlider.Value, (float)speedSlider.Value);
        input.GrabFocus();
    }

    int SelectedStyleId()
    {
        for (var i = 0; i < styleButtons.Count; i++)
        {
            if (styleButtons[i].ButtonPressed)
            {
                return i;
            }
        }
        return 0;
    }

    void OnSpeakerStateChanged(int state, string message)
    {
        var color = ((SpeakerState)state) switch
        {
            SpeakerState.Ready => OkColor,
            SpeakerState.Synthesizing => WarnColor,
            SpeakerState.Playing => AccentColor,
            SpeakerState.Error => ErrorColor,
            SpeakerState.Initializing => WarnColor,
            _ => MutedColor,
        };
        UpdateStatus(color, message);
        speakButton.Disabled = ((SpeakerState)state) is not (SpeakerState.Ready or SpeakerState.Synthesizing or SpeakerState.Playing);
    }

    void OnQueueChanged(int synthPending, int playPending)
    {
        var total = synthPending + playPending;
        queueLabel.Text = $"キュー {total} 件 (合成 {synthPending} / 再生 {playPending})";
        queueLabel.AddThemeColorOverride("font_color", total > 0 ? AccentColor : MutedColor);
    }

    void OnPlaybackStarted(string text, string styleName, float duration)
    {
        var styleId = SelectedStyleId();
        history.Insert(0, new HistoryEntry(text, styleId, (float)weightSlider.Value, (float)speedSlider.Value));
        historyList.AddItem($"[{styleName}] {text.Replace("\n", " ")} ({duration:0.0}s)", null, true);
        historyList.MoveItem(historyList.ItemCount - 1, 0);
        while (historyList.ItemCount > 40)
        {
            historyList.RemoveItem(40);
        }
    }

    void OnHistoryActivated(long index)
    {
        if (index < 0 || index >= history.Count)
        {
            return;
        }
        var e = history[(int)index];
        speaker.Speak(e.Text, e.StyleId, e.Weight, e.Speed);
    }

    void UpdateStatus(Color color, string message)
    {
        statusLabel.Text = message;
        statusLabel.AddThemeColorOverride("font_color", color);
    }

    // ------------------------------------------------------------------ UI ヘルパー

    static Label MakeLabel(Color color, string text) => MakeLabel(color, text, 15);

    static Label MakeLabel(Color color, string text, int size)
    {
        var label = new Label { Text = text };
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }

    static Control MakeSpacer() => new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

    static Control RightWrap(Control child)
    {
        var c = new CenterContainer();
        c.AddChild(child);
        return c;
    }

    static HSlider MakeSlider(float min, float max, float step, float value) => new()
    {
        MinValue = min,
        MaxValue = max,
        Step = step,
        Value = value,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };

    static HSeparator MakeHSeparator()
    {
        var s = new HSeparator();
        s.AddThemeConstantOverride("separation", 4);
        return s;
    }

    static StyleBoxFlat MakePanelStyle(Color bg, int radius, int border = 0, Color? borderColor = null) => new()
    {
        BgColor = bg,
        CornerRadiusBottomLeft = radius,
        CornerRadiusBottomRight = radius,
        CornerRadiusTopLeft = radius,
        CornerRadiusTopRight = radius,
        BorderWidthLeft = border,
        BorderWidthRight = border,
        BorderWidthTop = border,
        BorderWidthBottom = border,
        BorderColor = borderColor ?? BgColor,
    };

    static void StyleButton(Button b, Color accent, bool textDark = false)
    {
        var normal = MakePanelStyle(PanelColor, 7);
        var pressed = MakePanelStyle(accent, 7);
        var hover = MakePanelStyle(PanelColor.Lightened(0.06f), 7);
        b.AddThemeStyleboxOverride("normal", normal);
        b.AddThemeStyleboxOverride("hover", hover);
        b.AddThemeStyleboxOverride("pressed", pressed);
        b.AddThemeStyleboxOverride("focus", pressed);
        b.AddThemeStyleboxOverride("disabled", normal);
        b.AddThemeColorOverride("font_color", textDark ? BgColor : FgColor);
        // pressed/focus 時は必ずアクセント背景になるため文字は暗色
        b.AddThemeColorOverride("font_pressed_color", BgColor);
        b.AddThemeColorOverride("font_focus_color", textDark ? BgColor : FgColor);
        b.AddThemeColorOverride("font_hover_color", textDark ? BgColor : AccentColor);
        b.AddThemeColorOverride("font_hover_pressed_color", BgColor);
        b.AddThemeColorOverride("font_disabled_color", MutedColor);
    }
}

/// <summary>小さな fluent ヘルパ。</summary>
file static class UiExtensions
{
    public static T With<T>(this T node, Action<T> f) where T : Node
    {
        f(node);
        return node;
    }
}
