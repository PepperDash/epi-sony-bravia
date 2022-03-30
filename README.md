![PepperDash Logo](/images/logo_pdt_no_tagline_600.png)
# Sony Bravia Display Plugin

This is a plugin repo for Sony Bravia Displays.  At this time the plugin only supports RS232 control.

[Sony Bravia Display - RS232 API](https://pro-bravia.sony.net/develop/integrate/rs-232c/index.html)

## RS232 Specification

Verify the RS232 via Serial Connection control is enabled.  In testing, it was found the unit defaulted to RS232 via HDMI.

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
            "method": "comm",
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

At this time Simple IP control has not been implemented.

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
