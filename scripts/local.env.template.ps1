# SmartHome local developer config
# ─────────────────────────────────
# Copy this file to  scripts\local.env.ps1  (it is git-ignored) and fill in
# the values that match your machine.

# COM port the ESP32 is connected to (check Device Manager under Ports)
$env:SMARTHOME_COM_PORT      = "COM3"

# Full path to your Mosquitto installation directory (must contain mosquitto.exe)
$env:SMARTHOME_MOSQUITTO_DIR = "C:\Program Files\mosquitto"

# SMARTHOME_MQTT_BROKER is the address the DEVICE dials to reach this machine's broker,
# so it has to be an address the ESP32 can route to -- your LAN IP, not "localhost".
# Run-IntegrationTests.ps1 compares it against the BrokerHost constants compiled into the
# device projects and warns when they have drifted apart.
#
# Host-side tooling does NOT use it. The scripts always dial IPv4 loopback directly; see
# $SmartHomeLocalBrokerHost in Common.ps1 for why that is not configurable.
#
# The PORT is shared: both sides must agree on it.
$env:SMARTHOME_MQTT_BROKER   = "localhost"
$env:SMARTHOME_MQTT_PORT     = "1883"
