// callback methods from RequestService class
function renderQRCode(url) {
    document.getElementById('qrcode').style.display = "block";
    const qrImage = document.getElementById("qrcode").getElementsByTagName("img")[0];
    if (qrImage) qrImage.style.opacity = "1.0";
    qrcode.makeCode(url);
}
function showWalletRequest(requestType, pinCode, mobile) {
    const instructions = document.getElementById('wallet-instructions');
    const panel = document.getElementById('wallet-request');
    if (!panel || !instructions) return;
    instructions.hidden = true;
    panel.hidden = false;
    document.getElementById('wallet-request-heading').textContent = mobile
        ? 'Continue in your wallet'
        : requestType === 'selfie' ? 'Scan to take a selfie'
        : requestType === 'presentation' ? 'Scan to present' : 'Scan to issue';
    document.getElementById('wallet-request-help').textContent = mobile
        ? 'Return to this page to check the result.'
        : requestType === 'selfie' ? 'Scan with your phone to open the camera.'
        : 'Scan with Microsoft Authenticator or a compatible wallet.';
    const pin = document.getElementById('pinCode');
    if (pin) {
        pin.textContent = pinCode != null ? 'PIN code: ' + pinCode : '';
        pin.style.display = pinCode != null ? 'block' : 'none';
    }
}
function dimQRCode() {
    const qrImage = document.getElementById("qrcode").getElementsByTagName("img")[0];
    if (qrImage) qrImage.style.opacity = "0.1";
}
function hideQRCode() {
    document.getElementById("qrcode").style.display = "none";
    const qrImage = document.getElementById("qrcode").getElementsByTagName("img")[0];
    if (qrImage) qrImage.style.display = "none";
    var pinCode = document.getElementById('pinCode');
    if ( null != pinCode ) {
        pinCode.style.display = "none";
    }
    const panel = document.getElementById('wallet-request');
    const instructions = document.getElementById('wallet-instructions');
    if (panel && instructions) {
        panel.hidden = true;
        instructions.hidden = false;
    }
}
function displayMessage(msg) {
    document.getElementById("message-wrapper").style.display = "block";    
    document.getElementById('message').textContent = msg;
}
function drawQRCode(requestType, id, url, pinCode) {
    showWalletRequest(requestType, pinCode, false);
    renderQRCode(url);
    if (requestType == "presentation") {
        document.getElementById('verify-credential').style.display = "none";
        displayMessage("Waiting for QR code to be scanned");
    } else if (requestType == "issuance") {
        document.getElementById('issue-credential').style.display = "none";
        document.getElementById('take-selfie').style.display = "none";
        displayMessage("Waiting for QR code to be scanned");
    } else if (requestType == "selfie") {
        displayMessage("Waiting for QR code to be scanned with QR code reader app");
    }
}
function navigateToDeepLink(requestType, id, url, pinCode) {
    showWalletRequest(requestType, pinCode, true);
    const button = document.getElementById(requestType === 'presentation' ? 'verify-credential' : 'issue-credential');
    if (button && requestType !== 'selfie') {
        button.style.display = "none";
    }
    document.getElementById('check-result').style.display = "block";
    window.location.href = url;
}
function requestRetrieved(requestType) {
    dimQRCode();
    if (requestType == "presentation") {
        displayMessage("QR code scanned. Waiting for Verified ID credential to be shared from wallet...");
    } else {
        displayMessage("QR code scanned. Waiting for Verified ID credential to be added to wallet...");
    }
}
function presentationVerified(id, response) {
    hideQRCode();
    displayMessage("Presentation verified: " + JSON.stringify(response.claims));
    window.location = 'PresentationVerified?id=' + id;
}
function issuanceComplete(id, response) {
    hideQRCode();
    displayMessage("Issuance completed");
}
function selfieTaken(id, response) {
    hideQRCode();
    displayMessage("Selfie taken");
    document.getElementById('selfie').src = "data:image/jpeg;base64," + response.photo;
    document.getElementById('selfie').hidden = false;
    document.getElementById('issue-credential').disabled = !window.issuanceManifestReady;
    document.getElementById('imageUpload').disabled = false;
}
function requestError(requestType, response) {
    hideQRCode();
    console.log(JSON.stringify(response));
    displayMessage(`${requestType} error: ` + JSON.stringify(response));
    if (requestType === 'selfie') {
        document.getElementById('issue-credential').disabled = !window.issuanceManifestReady;
        document.getElementById('imageUpload').disabled = false;
    }
}
// method to post selfie taken before starting an issuance request
async function setUserPhoto() {
    if (!document.getElementById('selfie').hidden && document.getElementById('selfie').src != "") {
        await requestService.setUserPhoto(document.getElementById('selfie').src);
    }
}

function uploadImage(e) {
    return new Promise((resolve, reject) => {
        if (!e.target.files || !e.target.files.length) {
            resolve();
            return;
        }
        const file = e.target.files[0];
        if (!file.type.startsWith('image/') || file.size > 10 * 1024 * 1024) {
            reject(new Error('Choose an image smaller than 10 MB.'));
            return;
        }
        var reader = new FileReader();
        reader.onerror = () => reject(new Error('Unable to read the selected photo.'));
        reader.readAsDataURL(file);
        reader.onload = function () {
            var imageObj = new Image();
            imageObj.onerror = () => reject(new Error('The selected file is not a supported image.'));
            imageObj.onload = function (ev) {
                if (imageObj.naturalWidth < 200 || imageObj.naturalHeight < 200) {
                    reject(new Error('Choose a photo that is at least 200 by 200 pixels.'));
                    return;
                }
                var canvas = document.createElement("canvas");
                const scale = Math.min(1, 512 / Math.max(imageObj.naturalWidth, imageObj.naturalHeight));
                canvas.width = Math.round(imageObj.naturalWidth * scale);
                canvas.height = Math.round(imageObj.naturalHeight * scale);
                const context = canvas.getContext('2d');
                if (!context) {
                    reject(new Error('Unable to prepare the selected photo.'));
                    return;
                }
                context.drawImage(imageObj, 0, 0, canvas.width, canvas.height);
                document.getElementById("selfie").src = canvas.toDataURL('image/jpeg', 0.7);
                document.getElementById("selfie").hidden = false;
                resolve();
            }
            imageObj.src = reader.result;
        }
    });
}


// RequestService object that drives the interaction with backend APIs
// verifiedid.requestservice.client.js
var requestService = new RequestService(drawQRCode,
    navigateToDeepLink,
    requestRetrieved,
    presentationVerified,
    issuanceComplete,
    selfieTaken,
    requestError
);
// If to do console.log (a lot)
requestService.logEnabled = false;
