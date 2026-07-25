# Reads the OTA password from a local, gitignored text file and exports
# it as a build flag so both the C++ code (ArduinoOTA.setPassword)
# and PlatformIO's espota uploader (upload_flags) can use it.
#
# Expected file: firmware/core2/ota_password.txt
#   Contains one line: the plaintext OTA password
#   Example:  my-ota-secret-123
#
# If the file is missing, the script prints a warning and falls back
# to the well-known example password "opendash32" so the build doesn't
# break — BUT this is NOT secure and you should create the file.
#
# To set up (do this once):
#   echo my-real-password > firmware/core2/ota_password.txt
#   git add firmware/core2/ota_password.txt --intent-to-add  (optional)

import os
Import("env")

PASSWORD_FILE = os.path.join(env.subst("$PROJECT_DIR"), "ota_password.txt")

password = "opendash32"  # fallback — CHANGE THIS for production
try:
    with open(PASSWORD_FILE, "r") as f:
        pw = f.read().strip()
        if pw:
            password = pw
            print("OTA: using password from ota_password.txt")
        else:
            print("OTA: ota_password.txt is empty, using fallback password")
except FileNotFoundError:
    print("OTA: ota_password.txt not found, using fallback password (CREATE THIS FILE for security)")

# Inject into C++ build flags — pass as a plain string literal.
# CPPDEFINES with a tuple builds `-DOTA_PASSWORD='"password"'`.
env.Append(CPPDEFINES=[("OTA_PASSWORD", password)])

# Inject into espota upload flags so PlatformIO passes --auth=<password>
env.Append(UPLOAD_FLAGS=["--auth=" + password])