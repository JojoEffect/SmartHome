# SmartHome local developer config
# ─────────────────────────────────
# Copy this file to  scripts\local.env.ps1  (it is git-ignored) and fill in
# the values that match your machine.

# COM port the ESP32 is connected to (check Device Manager under Ports)
$env:SMARTHOME_COM_PORT      = "COM3"

# Full path to your Mosquitto installation directory (must contain mosquitto.exe)
$env:SMARTHOME_MOSQUITTO_DIR = "C:\Program Files\mosquitto"

# MQTT broker host/port used by the device and the local subscriber.
# Only change these if you are not using the default Mosquitto localhost setup.
$env:SMARTHOME_MQTT_BROKER   = "localhost"
$env:SMARTHOME_MQTT_PORT     = "1883"
