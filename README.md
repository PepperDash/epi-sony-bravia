![PepperDash Essentials Plugin Logo](/images/essentials-plugin-blue.png)

![PepperDash Essentials](https://img.shields.io/badge/PepperDash%20Essentials-≥%20v2.20.0-blue)
![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-512BD4)
![Crestron](https://img.shields.io/badge/Crestron-4--Series-lightgrey)
![License](https://img.shields.io/badge/license-MIT-green)

# Sony Bravia Display Plugin

This is a plugin repo for Sony Bravia Displays.  At this time the plugin only supports RS232 control.

[Sony Bravia Display - RS232 API](https://pro-bravia.sony.net/develop/integrate/rs-232c/index.html)

## RS232 Specification

Verify the RS232 via Serial Connection control is enabled.  In testing, it was found the unit defaulted to RS232 via HDMI.

Set device to **Normal Mode**, do not put the device in **Pro Mode** or **Pro Settings Mode**.  If they are put in either of the **Pro** modes requires **Remote Commander** out of **Pro** mode.

| Property     | Value |
| ------------ | ----- |
| Baudrate     | 9,600 |
| Data Bits    | 8     |
| Parity       | N/A   |
| Start Bits   | 1     |
| Stop Bits    | 1     |
| Flow Control | N/A   |

### Essentials RS232 Device Configuration

```json
{
    "key": "display-1",
    "name": "Display",
    "type": "sonybravia",
    "group": "display",
    "properties": {
        "control": {
            "method": "com",
            "controlPortDevKey": "processor",
            "controlPortNumber": 1,
            "comParams": {
                "protocol": "RS232",
                "baudRate": 9600,
                "dataBits": 8,
                "stopBits": 1,
                "parity": "None",
                "softwareHandshake": "None",
                "hardwareHandshake": "None",
                "pacing": 0
            }
        }
    }
}
```

## Simple IP Specification

| Property | Value |
| -------- | ----- |
| Port     | 20060 |


### Essentials Simple IP Device Configuration

Simple IP control is currently **under development**

```json
{
    "key": "display-1",
    "name": "Display",
    "type": "sonybravia",
    "group": "display",
    "properties": {
        "control": {
            "method": "tcpip",
            "tcpSshProperties": {
                "address": "",
                "port": 20060,
                "username": "",
                "password": "",
                "autoReconnect": true,
                "autoReconnectIntervalMs": 10000
            }                        
        }
    }
}
```

## IR Remote Control

The RS232 protocol has no remote-key commands (arrows, keypad, transport, app buttons), so remote control is a
separate IR device, type `sonyBraviaIr`. It sends Crestron standard command names (`UP_ARROW`, `DN_ARROW`, `HDMI_1`,
`NETFLIX`, `AMAZON_VIDEO`, ...), and the `.ir` file maps each one to the TV's code. The `.ir` file goes in the
program's `ir` folder.

It implements `IDPad`, `INumericKeypad`, `ITransport`, `IChannel`, `IColor` and `IHasInputs<string>`. The inputs are
`hdmi1`–`hdmi4`, `netflix` and `primeVideo`.

### Essentials IR Device Configuration

Usually paired with the RS232 `sonybravia` device as a control-only remote. The RS232 device keeps power, inputs and
feedback, and this device sends remote keys only:

```json
{
    "key": "display-1-ir",
    "name": "Display Remote",
    "type": "sonyBraviaIr",
    "group": "display",
    "properties": {
        "remoteOnly": true,
        "control": {
            "method": "ir",
            "controlPortDevKey": "rx-display-1",
            "controlPortNumber": 1,
            "irFile": "<Sony Bravia IR file>.ir"
        }
    }
}
```

| Property | Type | Default | Description |
| -------- | ---- | ------- | ----------- |
| `remoteOnly` | bool | `false` | `true`: never send power, including the power-on an input or app select would otherwise send first. Set it whenever another driver owns the display's power. Leave it `false` only when IR is the display's sole driver. |
| `warmingTimeMs` | uint | `8000` | Warm-up wait before an input select is re-sent after power-on. Not used when `remoteOnly` is `true`. |
| `coolingTimeMs` | uint | `8000` | Cool-down time after power-off. |
| `friendlyNames` | array | `[]` | `{ "inputKey", "name", "hideInput" }` entries that rename or hide inputs. |
| `irCommandMap` | object | `{}` | Maps a standard command (`"SELECT"`) or input key (`"hdmi1"`) to the name this `.ir` file uses. The target can be a standard command or the file's button label. Keys are case-insensitive. |

`controlPortDevKey` is any device with IR ports, for example the display's NVX receiver, a CEN-IO-IR-104 or the
processor. Home, Back and Enter have no core interface, so they're exposed as methods for `devjson` only.

### Checking an IR file

IR files don't all use the same names. At startup the device logs one warning listing every command it sends that
the file has no standard command for, for example:

```
IR file 'Sony KD46XBR3.ir' has no standard command for: HOME, BACK, GUIDE, ... Those buttons won't work unless irCommandMap maps them to names the file uses
```

Map the ones the file has under another name; the rest have no code in the file.

For example, the `FlatPanelDisplay_Sony_K-55XR50_IR.ir` driver (2025 Bravia, RMF-TX820U remote; included in
[`src/SonyBraviaIr/`](src/SonyBraviaIr/FlatPanelDisplay_Sony_K-55XR50_IR.ir)) resolves the
arrows, `SELECT`, `MENU`, `HOME`, `BACK`, `GUIDE`, `HDMI_1`–`4`, `NETFLIX`, volume and channel directly. It has a single
`PLAY_PAUSE` key, labels Prime Video `PRIME_VIDEO`, and uses `BACK` where other remotes have `EXIT`. The remote has no
number keys, so leave the keypad off for users (`displayControlsUserGroups.numpad`).

```json
"irCommandMap": {
    "PLAY": "PLAY_PAUSE",
    "PAUSE": "PLAY_PAUSE",
    "primeVideo": "PRIME_VIDEO",
    "EXIT": "BACK"
}
```

Older files differ more. `Sony KD46XBR3.ir` (2006 Bravia) maps its OK key to `ENTER` rather than `SELECT` and
labels its HDMI inputs `INPUT_7_(HDMI)`/`INPUT_8_(HDMI)`, so it needs `"SELECT": "ENTER"`, `"hdmi1": "INPUT_7_(HDMI)"`
and `"hdmi2": "INPUT_8_(HDMI)"`. It has no app, transport or colour keys at all.

To see every name a file contains, set the debug level to verbose (`appdebug:1 2`) and run
`devjson:1 {"deviceKey":"<key>-ir","methodName":"PrintAvailableCommands","params":[]}`.

## Mock Display (bench testing without hardware)

The `mockSonyBravia` type builds the real Sony BRAVIA device on an in-process mock panel that speaks the RS232 protocol. Power, warm-up and cool-down, inputs, volume, mute, picture mode and polling all run through the same code as with real hardware, so the room and the touchpanel behave the same way. To switch an existing display to the mock, change only `type`. The type was named `sonybraviamock` before 2026-10-19, and that name still works as an alias. Any `control` block is ignored, and `activeInputs`, `availablePictureModes`, `warmingTimeMs` and the other properties work as usual.

```json
{
    "key": "display-1",
    "name": "Display",
    "type": "mockSonyBravia",
    "group": "display",
    "properties": {
        "mock": {                       // optional; these are the defaults
            "responseDelayMs": 50,      // delay before each reply; keep it under 100
            "powerOn": false,           // starting power state
            "input": "hdmi1",           // hdmi1-hdmi5, video1-video3, component1-component3, vga1
            "volume": 20,               // starting raw volume
            "maxVolume": 255,           // higher levels answer "over maximum"
            "muted": false
        }
    }
}
```

Like a real panel, the mock answers "command cancelled" to everything except power while it is off. Picture mode has no query, so its reply is only an acknowledgement.

Use the `mocksony` console command to make changes at the panel, or to inject faults. The device picks up panel-side changes on its next poll.

```
mocksony display-1 status
mocksony display-1 power on|off
mocksony display-1 input hdmi1|...|vga1
mocksony display-1 volume <raw level>
mocksony display-1 mute on|off
mocksony display-1 fault none|error|silent    // error: answer everything "command cancelled"; silent: stop answering (goes offline)
```

## Device Bridging

### Essentials Device Bridge Configuration

```json
{
    "key": "plugin-bridge1",
    "uid": 39,
    "name": "Plugin Bridge",
    "group": "api",
    "type": "eiscApiAdvanced",
    "properties": {
        "control": {
            "tcpSshProperties": {
                "address": "127.0.0.2",
                "port": 0
            },
            "ipid": "B2",
            "method": "ipidTcp"
        },
        "devices": [
            {
                "deviceKey": "display-1",
                "joinStart": 1
            }
        ]
    }
}
```

### Essentials Bridge Join Map

The join map below documents the commands implemented in this plugin.

### Digitals

| Input                         | I/O | Output                    |
| ----------------------------- | --- | ------------------------- |
| Power Off                     | 1   | Power Off Fb              |
| Power On                      | 2   | Power On Fb               |
|                               | 3   | Is Two Display Fb         |
| Input 1 Select [HDMI 1]       | 11  | Input 1 Fb [HDMI 1]       |
| Input 2 Select [HDMI 2]       | 12  | Input 2 Fb [HDMI 2]       |
| Input 3 Select [HDMI 3]       | 13  | Input 3 Fb [HDMI 3]       |
| Input 4 Select [HDMI 4]       | 14  | Input 4 Fb [HDMI 4]       |
| Input 5 Select [HDMI 5]       | 15  | Input 5 Fb [HDMI 5]       |
| Input 6 Select [PC]           | 16  | Input 6 Fb [PC]           |
| Input 7 Select [Video 1]      | 17  | Input 7 Fb [Video 1]      |
| Input 8 Select [Video 2]      | 18  | Input 8 Fb [Video 2]      |
| Input 9 Select [Video 3]      | 19  | Input 9 Fb [video 3]      |
| Input 10 Select [Component 3] | 20  | Input 10 Fb [Component 1] |
|                               | 40  | Button 1 Visibility Fb    |
|                               | 41  | Button 2 Visibility Fb    |
|                               | 42  | Button 3 Visibility Fb    |
|                               | 43  | Button 4 Visibility Fb    |
|                               | 44  | Button 5 Visibility Fb    |
|                               | 45  | Button 6 Visibility Fb    |
|                               | 46  | Button 7 Visibility Fb    |
|                               | 47  | Button 8 Visibility Fb    |
|                               | 48  | Button 9 Visibility Fb    |
|                               | 49  | Button 10 Visibility Fb   |
|                               | 50  | Display Online Fb         |

### Analogs

| Input                      | I/O | Output                 |
| -------------------------- | --- | ---------------------- |
| Input Number Select [1-10] | 11  | Input Number Fb [1-10] |

### Serials

| Input | I/O | Output                      |
| ----- | --- | --------------------------- |
|       | 1   | Display Name                |
|       | 11  | Input 1 Name [HDMI 1]       |
|       | 12  | Input 2 Name [HDMI 2]       |
|       | 13  | Input 3 Name [HDMI 3]       |
|       | 14  | Input 4 Name [HDMI 4]       |
|       | 15  | Input 5 Name [HDMI 5]       |
|       | 16  | Input 6 Name [PC]           |
|       | 17  | Input 7 Name [Video 1]      |
|       | 18  | Input 8 Name [Video 2]      |
|       | 19  | Input 9 Name [Video 3]      |
|       | 20  | Input 10 Name [Component 1] |



## DEVJSON Commands

When using DEVJSON commands update the program index `devjson:{programIndex}` and `deviceKey` values to match the testing environment.

```json
devjson:1 {"deviceKey":"display-1", "methodName":"PowerOn", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"PowerOff", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"PowerToggle", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"PowerPoll", "params":[]}

Devjson:1 {"deviceKey":"display-1", "methodName":"ListRoutingInputPorts", "params": []}

devjson:1 {"deviceKey":"display-1", "methodName":"InputHdmi1", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputHdmi2", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputHdmi3", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputHdmi4", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputHdmi5", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputVideo1", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputVideo2", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputVideo3", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputComponent1", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputComponent2", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputComponent3", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputVga1", "params":[]}
devjson:1 {"deviceKey":"display-1", "methodName":"InputPoll", "params":[]}
```
<!-- START Minimum Essentials Framework Versions -->
### Minimum Essentials Framework Versions

- 2.20.0
- 2.20.0
- 2.20.0
<!-- END Minimum Essentials Framework Versions -->
<!-- START Config Example -->
### Config Example

```json
{
    "key": "GeneratedKey",
    "uid": 1,
    "name": "GeneratedName",
    "type": "SonyBravia",
    "group": "Group",
    "properties": {
        "CommunicationMonitorProperties": "SampleValue",
        "Control": "SampleValue",
        "warmingTimeMs": 0,
        "coolingTimeMs": 0,
        "ForceRs232": true,
        "maxVolumeLevel": "SampleValue",
        "activeInputs": [
            {
                "key": "SampleString",
                "name": "SampleString"
            }
        ],
        "availablePictureModes": [
            {
                "key": "SampleString",
                "name": "SampleString"
            }
        ]
    }
}
```
<!-- END Config Example -->
<!-- START Supported Types -->

<!-- END Supported Types -->
<!-- START Join Maps -->

<!-- END Join Maps -->
<!-- START Interfaces Implemented -->
### Interfaces Implemented

- ICommunicationMonitor
- IBridgeAdvanced
- IInputHdmi1
- IInputHdmi2
- IInputHdmi3
- IInputHdmi4
- IInputVga1
- IOnline
- IBasicVolumeWithFeedbackAdvanced
- IHasPowerControlWithFeedback
- IRoutingSinkWithSwitchingWithInputPort
- IHasInputs<string>
- IKeyName
- ISelectableItems<string>
- IQueueMessage
- IBasicCommunication
- IBasicVolumeControls
- IDPad
- INumericKeypad
- ITransport
- IChannel
- IColor
<!-- END Interfaces Implemented -->
<!-- START Base Classes -->
### Base Classes

- MessengerBase
- TwoWayDisplayBase
- DisplayBase
<!-- END Base Classes -->
<!-- START Public Methods -->
### Public Methods

- public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
- public void PowerPoll()
- public void ListRoutingInputPorts()
- public void BuildInputRoutingPorts()
- public void InputHdmi1()
- public void InputHdmi2()
- public void InputHdmi3()
- public void InputHdmi4()
- public void InputHdmi5()
- public void InputVideo1()
- public void InputVideo2()
- public void InputVideo3()
- public void InputComponent1()
- public void InputComponent2()
- public void InputComponent3()
- public void InputVga1()
- public void ResetPolling()
- public void SendRs232Command(byte[] command)
- public void EnqueueCommand(IQueueMessage command)
- public void Dispatch()
- public void SetInput(string selector)
- public void MuteOn()
- public void MuteOff()
- public void MuteToggle()
- public void SetVolume(ushort level)
- public void VolumeUp(bool pressRelease)
- public void VolumeDown(bool pressRelease)
- public void PictureModeVivid()
- public void PictureModeStandard()
- public void PictureModeCinema()
- public void PictureModeCinema2()
- public void PictureModeCustom()
- public void PictureModeSports()
- public void PictureModeGame()
- public void PictureModeGraphics()
- public void PictureModeToggle()
- public void Select()
- public void Dispatch()
- public void Dispatch()
- public void Connect()
- public void Disconnect()
- public void SendText(string text)
- public void SendBytes(byte[] bytes)
- public void SetPower(bool on)
- public bool SetInput(string inputKey)
- public void SetVolume(int level)
- public void SetMute(bool muted)
- public string Describe()
- public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
- public void SendIrCommand(string cmd)
- public void VolumeUp(bool pressRelease)
- public void VolumeDown(bool pressRelease)
- public void MuteToggle()
- public void Up(bool pressRelease)
- public void Down(bool pressRelease)
- public void Left(bool pressRelease)
- public void Right(bool pressRelease)
- public void Select(bool pressRelease)
- public void Menu(bool pressRelease)
- public void Exit(bool pressRelease)
- public void Digit0(bool pressRelease)
- public void Digit1(bool pressRelease)
- public void Digit2(bool pressRelease)
- public void Digit3(bool pressRelease)
- public void Digit4(bool pressRelease)
- public void Digit5(bool pressRelease)
- public void Digit6(bool pressRelease)
- public void Digit7(bool pressRelease)
- public void Digit8(bool pressRelease)
- public void Digit9(bool pressRelease)
- public void KeypadAccessoryButton1(bool pressRelease)
- public void KeypadAccessoryButton2(bool pressRelease)
- public void Play(bool pressRelease)
- public void Pause(bool pressRelease)
- public void Stop(bool pressRelease)
- public void Rewind(bool pressRelease)
- public void FFwd(bool pressRelease)
- public void ChapMinus(bool pressRelease)
- public void ChapPlus(bool pressRelease)
- public void Record(bool pressRelease)
- public void ChannelUp(bool pressRelease)
- public void ChannelDown(bool pressRelease)
- public void LastChannel(bool pressRelease)
- public void Guide(bool pressRelease)
- public void Info(bool pressRelease)
- public void Red(bool pressRelease)
- public void Green(bool pressRelease)
- public void Yellow(bool pressRelease)
- public void Blue(bool pressRelease)
- public void Home(bool pressRelease)
- public void Back(bool pressRelease)
- public void Enter(bool pressRelease)
- public void Select()
<!-- END Public Methods -->
<!-- START Bool Feedbacks -->
### Bool Feedbacks

- IsOnline
- MuteFeedback
<!-- END Bool Feedbacks -->
<!-- START Int Feedbacks -->
### Int Feedbacks

- VolumeLevelFeedback
<!-- END Int Feedbacks -->
<!-- START String Feedbacks -->
### String Feedbacks

- PictureModeFeedback
<!-- END String Feedbacks -->
