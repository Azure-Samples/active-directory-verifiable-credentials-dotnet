// callback methods from RequestService class
function renderQRCode(url) {
    document.getElementById('qrcode').style.display = "block";
    document.getElementById("qrcode").getElementsByTagName("img")[0].style.opacity = "1.0";
    qrcode.makeCode(url);
}
function dimQRCode() {
    document.getElementById("qrcode").getElementsByTagName("img")[0].style.opacity = "0.1";
}
function hideQRCode() {
    document.getElementById("qrcode").style.display = "none";
    document.getElementById("qrcode").getElementsByTagName("img")[0].style.display = "none";
    var pinCode = document.getElementById('pinCode');
    if ( null != pinCode ) {
        pinCode.style.display = "none";
    }
}
function displayMessage(msg) {
    document.getElementById("message-wrapper").style.display = "block";    
    document.getElementById('message').innerHTML = msg;
}
function drawQRCode(requestType, id, url, pinCode) {
    renderQRCode(url);
    if (requestType == "presentation") {
        document.getElementById('verify-credential').style.display = "none";
        displayMessage("Waiting for QR code to be scanned");
    } else if (requestType == "issuance") {
        document.getElementById('issue-credential').style.display = "none";
        document.getElementById('take-selfie').style.display = "none";
        displayMessage("Waiting for QR code to be scanned");
        if ( pinCode != undefined ) {
            document.getElementById('pinCode').innerHTML = "Pin code: " + pinCode;
            document.getElementById('pinCode').style.display = "block";
        }
    } else if (requestType == "selfie") {
        displayMessage("Waiting for QR code to be scanned with QR code reader app");
    }
}
function navigateToDeepLink(requestType, id, url) {
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
    displayMessage("Presentation verified: <br/><br/>" + JSON.stringify(response.claims));
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
    document.getElementById('selfie').style.display = "block";
    document.getElementById('issue-credential').disabled = false;
    document.getElementById('imageUpload').style.display = "";
}
function requestError(requestType, response) {
    hideQRCode();
    console.log(JSON.stringify(response));
    displayMessage(`${requestType} error: ` + JSON.stringify(response));
    if (requestType === 'selfie') {
        document.getElementById('issue-credential').disabled = false;
        document.getElementById('imageUpload').style.display = "";
    }
}
// method to post selfie taken before starting an issuance request
async function setUserPhoto() {
    if ("none" != document.getElementById('selfie').style.display && document.getElementById('selfie').src != "") {
        await requestService.setUserPhoto(document.getElementById('selfie').src);
    }
}

function hideShowPhotoElements(val) {
    document.getElementById("take-selfie").style.display = val;
    document.getElementById("imageUpload").style.display = val;
    document.getElementById("photo-help").style.display = val;
}

function uploadImage(e) {
    return new Promise((resolve, reject) => {
        if (!e.target.files || !e.target.files.length) {
            resolve();
            return;
        }
        var reader = new FileReader();
        reader.onerror = () => reject(new Error('Unable to read the selected photo.'));
        reader.readAsDataURL(e.target.files[0]);
        reader.onloadend = function (e) {
            if (reader.error) return;
            var imageObj = new Image();
            imageObj.onerror = () => reject(new Error('The selected file is not a supported image.'));
            imageObj.onload = function (ev) {
                var canvas = document.createElement("canvas");
                const scale = Math.min(1, 512 / Math.max(imageObj.naturalWidth, imageObj.naturalHeight));
                canvas.width = Math.round(imageObj.naturalWidth * scale);
                canvas.height = Math.round(imageObj.naturalHeight * scale);
                canvas.getContext('2d').drawImage(imageObj, 0, 0, canvas.width, canvas.height);
                document.getElementById("selfie").src = canvas.toDataURL('image/jpeg', 0.7);
                document.getElementById("selfie").style.display = "block";
                resolve();
            }
            imageObj.src = e.target.result;
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
